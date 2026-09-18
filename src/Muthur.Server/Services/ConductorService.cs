using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>One validator session the conductor intends to start.</summary>
/// <param name="AvoidHarness">The harness that built the task, if known: a different vendor checks the work where one is free.</param>
public sealed record ConductorAssignment(int TaskId, string TaskKey, string TaskTitle, string Project, string RoleKey, string? AvoidHarness);

/// <summary>Starts one validator session. Faked in tests; the real one launches a harness through <c>AgentLauncher</c>.</summary>
public interface IValidatorSessionLauncher
{
    Task StartAsync(ConductorAssignment assignment, CancellationToken ct = default);
}

/// <summary>
/// Staffs validation so a task that passes needs nobody awake.
/// <para>
/// The conductor adds no state to the task machine it serves. It only notices a task sitting in
/// <see cref="TaskState.Validating"/> whose required role nobody holds, and starts a session to hold it. A failed
/// task is already back with its owner; if that session has gone, the claim lapses and the task is staffable
/// again on a later pass — the bounce-back loop closes on machinery that already existed.
/// </para>
/// </summary>
public sealed class ConductorService(Ledger ledger, MuthurOptions options, TimeProvider clock, IValidatorSessionLauncher launcher)
{
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly HashSet<string> _running = [];
    private readonly HashSet<int> _escalated = [];

    // F3: "what it last did" - a founder who turned this on overnight needs to know it ran at all.
    private DateTimeOffset? _lastPass;
    private string? _lastAction;

    /// <summary>
    /// Consecutive failures to <em>start</em> a session, per task and role, and when that pair last gave up.
    /// <para>
    /// A validator that ran and said no is progress; a session that could not start at all is the organization
    /// wedged, and retrying it every interval all night writes thousands of ledger events while `status` reads
    /// perfectly healthy. So a pair stops after <c>ConductorMaxAttempts</c> — but it stops <em>half-open</em>:
    /// one probe is let through every <c>ConductorStallProbeMinutes</c>. Whatever was wrong is usually fixed from
    /// outside the hub — a quota cleared, a harness installed, a repository remounted — and the founder should not
    /// have to know an incantation to resume. A probe that starts clears the count; one that fails re-arms the
    /// cooldown without saying anything further.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, Stall> _launchFailures = [];

    private sealed record Stall(int Failures, DateTimeOffset? StalledAt, bool Announced);

    /// <summary>
    /// Whether staffing is on. The hub is a local process that is off for hours, so this lives in the database:
    /// a founder who turned it on must not find it silently off after the next restart.
    /// </summary>
    private Task<bool> EnabledAsync(CancellationToken ct) =>
        ledger.ReadAsync(async (db, _) =>
            await db.Meta.Where(e => e.Key == MetaEntry.ConductorEnabled).Select(e => e.Value).SingleOrDefaultAsync(ct)
                is { } stored ? stored == "true" : options.ConductorEnabled, ct);

    /// <summary>Sessions the conductor believes it has running, by "T-n/role".</summary>
    public int RunningCount { get { lock (_running) return _running.Count; } }

    public async Task<ConductorStatusDto> StatusAsync(CancellationToken ct = default) => new(
        await EnabledAsync(ct),
        RunningCount,
        options.ConductorMaxSessions,
        options.ConductorSessionMinutes,
        options.ConductorMaxAttempts,
        options.ConductorIntervalSeconds,
        options.ConductorStallProbeMinutes,
        _lastPass,
        _lastAction);

    /// <summary>The founder turns staffing on and off; the decision is in the ledger like any other.</summary>
    public async Task<ConductorStatusDto> SetEnabledAsync(Caller caller, bool enabled, CancellationToken ct = default)
    {
        var was = await EnabledAsync(ct);
        await ledger.MutateAsync(caller, async m =>
        {
            var row = await m.Db.Meta.SingleOrDefaultAsync(e => e.Key == MetaEntry.ConductorEnabled, ct);
            if (row is null) m.Db.Meta.Add(new MetaEntry { Key = MetaEntry.ConductorEnabled, Value = enabled ? "true" : "false" });
            else row.Value = enabled ? "true" : "false";
            if (was != enabled)
                m.Record(enabled ? "conductor.on" : "conductor.off", payload: new { maxSessions = options.ConductorMaxSessions });
            // Turning it on is the founder saying "try again"; a stall from before that is not their answer.
            if (enabled) lock (_launchFailures) _launchFailures.Clear();
        }, ct);
        return await StatusAsync(ct);
    }

    /// <summary>
    /// What the conductor would staff right now, most urgent first. Separated from starting anything so the
    /// decision can be tested without launching a process.
    /// </summary>
    public async Task<IReadOnlyList<ConductorAssignment>> PlanAsync(CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct)) return [];
        // Callers that already know staffing is on pass it in; PlanAsync on its own re-reads it.

        return await ledger.ReadAsync<IReadOnlyList<ConductorAssignment>>(async (db, now) =>
        {

            // Only 'validating'. A blocked task is waiting on the founder, and staffing it would waste a session
            // on work that cannot move.
            var tasks = await db.Tasks.Include(t => t.Project)
                .Where(t => t.State == TaskState.Validating)
                .OrderByDescending(t => t.Priority).ThenBy(t => t.Id)
                .ToListAsync(ct);
            if (tasks.Count == 0) return [];

            var ids = tasks.Select(t => t.Id).ToList();
            var pending = await db.TaskValidations
                .Where(v => ids.Contains(v.TaskId) && v.Verdict == Verdict.Pending)
                .ToListAsync(ct);
            if (pending.Count == 0) return [];

            var held = await db.RoleHolds.Where(h => h.LeaseExpires > now).Select(h => h.RoleKey).ToListAsync(ct);
            var validatorRoles = await db.Roles.Where(r => r.IsValidator).Select(r => r.Key).ToListAsync(ct);

            // A task that has failed too many times is a judgment call, and judgment calls go to the founder.
            var failures = await db.Events
                .Where(e => e.Type == "validation.failed" && e.TaskId != null && ids.Contains(e.TaskId!.Value))
                .GroupBy(e => e.TaskId!.Value)
                .Select(g => new { TaskId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

            // "harness/model" of whoever declared the task implemented.
            var built = await db.Events
                .Where(e => e.Type == "task.implemented" && e.TaskId != null && ids.Contains(e.TaskId!.Value) && e.ActorModel != null)
                .GroupBy(e => e.TaskId!.Value)
                .Select(g => new { TaskId = g.Key, Model = g.OrderByDescending(e => e.Seq).First().ActorModel })
                .ToDictionaryAsync(x => x.TaskId, x => x.Model, ct);

            var plan = new List<ConductorAssignment>();
            foreach (var task in tasks)
            {
                if (failures.GetValueOrDefault(task.Id) >= options.ConductorMaxAttempts) continue;
                foreach (var validation in pending.Where(v => v.TaskId == task.Id))
                {
                    if (held.Contains(validation.ValidatorKey)) continue;
                    if (!validatorRoles.Contains(validation.ValidatorKey)) continue;   // a role that no longer exists
                    var key = $"{Wire.TaskId(task.Id)}/{validation.ValidatorKey}";
                    lock (_running)
                        if (_running.Contains(key)) continue;
                    lock (_launchFailures)
                        if (_launchFailures.GetValueOrDefault(key) is { StalledAt: { } stalled } &&
                            stalled + TimeSpan.FromMinutes(options.ConductorStallProbeMinutes) > now)
                            continue;

                    plan.Add(new ConductorAssignment(
                        task.Id, Wire.TaskId(task.Id), task.Title, task.Project?.Key ?? "",
                        validation.ValidatorKey,
                        built.GetValueOrDefault(task.Id) is { Length: > 0 } model ? model.Split('/')[0] : null));
                }
            }
            return plan;
        }, ct);
    }

    /// <summary>One pass: start what the plan asks for, up to the session budget. Returns how many it started.</summary>
    public async Task<int> RunPassAsync(CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct)) return 0;
        if (!await _pass.WaitAsync(0, ct)) return 0;   // a slow pass must never overlap the next tick
        try
        {
            _lastPass = clock.GetUtcNow();
            await EscalateExhaustedAsync(ct);

            var started = 0;
            foreach (var assignment in await PlanAsync(ct))
            {
                var key = $"{assignment.TaskKey}/{assignment.RoleKey}";
                lock (_running)
                {
                    if (_running.Count >= options.ConductorMaxSessions) break;
                    if (!_running.Add(key)) continue;
                }

                await ledger.MutateAsync(Caller.Founder, m =>
                {
                    m.Record("conductor.staffing", assignment.TaskId,
                        new { role = assignment.RoleKey, avoidHarness = assignment.AvoidHarness });
                    return Task.CompletedTask;
                }, ct);

                _lastAction = $"staffed {assignment.TaskKey} for {assignment.RoleKey}";
                _ = RunSessionAsync(assignment, key);
                started++;
            }
            return started;
        }
        finally { _pass.Release(); }
    }

    private async Task RunSessionAsync(ConductorAssignment assignment, string key)
    {
        try
        {
            await launcher.StartAsync(assignment, CancellationToken.None);
            lock (_launchFailures) _launchFailures.Remove(key);   // including a probe: the pair is working again
        }
        catch (Exception ex)
        {
            Stall stall;
            lock (_launchFailures)
            {
                var previous = _launchFailures.GetValueOrDefault(key);
                var failures = previous is null ? 1 : previous.Failures + 1;
                var stalling = failures >= options.ConductorMaxAttempts;
                stall = new Stall(failures, stalling ? clock.GetUtcNow() : null, previous?.Announced == true);
                _launchFailures[key] = stall with { Announced = stall.Announced || stalling };
            }

            await ledger.MutateAsync(Caller.Founder, m =>
            {
                m.Record("conductor.failed", assignment.TaskId, new { role = assignment.RoleKey, error = ex.Message, attempt = stall.Failures });
                if (stall.StalledAt is not null && !stall.Announced)
                {
                    // Stop rather than degrade, and say so once: the founder's only other signal is that nothing shipped.
                    m.Record("conductor.stalled", assignment.TaskId, new { role = assignment.RoleKey, error = ex.Message });
                    MessageService.PostFromHub(m, Recipient.Founder, null,
                        $"The conductor could not start a validator for {assignment.TaskKey} ({assignment.RoleKey}) " +
                        $"{stall.Failures} times and has stopped trying: {ex.Message}" + Environment.NewLine +
                        $"Fix the cause and it retries by itself within {options.ConductorStallProbeMinutes} " +
                        (options.ConductorStallProbeMinutes == 1 ? "minute; " : "minutes; ") +
                        "`muthur conductor off --founder && muthur conductor on --founder` retries at once.",
                        assignment.TaskId);
                    _lastAction = $"gave up starting {assignment.RoleKey} for {assignment.TaskKey}: {ex.Message}";
                }
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        finally
        {
            lock (_running) _running.Remove(key);
        }
    }

    /// <summary>
    /// The first line of a failed verdict's evidence, capped. A founder's notification says which validator said no
    /// and roughly why; the evidence in full is one command away and belongs there, not in a message body.
    /// </summary>
    internal static string FirstLineOfEvidence(string payloadJson)
    {
        const int Limit = 140;
        string? evidence = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("evidence", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                evidence = value.GetString();
        }
        catch (System.Text.Json.JsonException) { }

        var line = (evidence ?? "").ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim().TrimStart('#').Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(line)) return "(no evidence)";
        return line.Length <= Limit ? line : line[..(Limit - 1)].TrimEnd() + "…";
    }

    /// <summary>A task that keeps failing stops being restaffed and becomes a question for the founder, once.</summary>
    private Task EscalateExhaustedAsync(CancellationToken ct) =>
        ledger.MutateAsync(Caller.Founder, async m =>
        {
            var validating = await m.Db.Tasks.Where(t => t.State == TaskState.Validating).Select(t => t.Id).ToListAsync(ct);
            if (validating.Count == 0) return;

            var failures = await m.Db.Events
                .Where(e => e.Type == "validation.failed" && e.TaskId != null && validating.Contains(e.TaskId!.Value))
                .GroupBy(e => e.TaskId!.Value)
                .Select(g => new { TaskId = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            foreach (var row in failures.Where(f => f.Count >= options.ConductorMaxAttempts))
            {
                lock (_escalated)
                    if (!_escalated.Add(row.TaskId)) continue;

                var task = await m.Db.Tasks.FirstAsync(t => t.Id == row.TaskId, ct);
                var events = await m.Db.Events
                    .Where(e => e.Type == "validation.failed" && e.TaskId == task.Id)
                    .OrderBy(e => e.Seq).Select(e => new { e.At, e.Actor, e.PayloadJson }).ToListAsync(ct);
                var verdicts = events.Select(e => $"- {e.At:yyyy-MM-dd HH:mm} {e.Actor}: {FirstLineOfEvidence(e.PayloadJson)}");

                // One line per verdict. The founder reads this on a card; the evidence itself is whole in
                // `muthur task show`, and pasting it here turns a notification into kilobytes of escaped JSON.
                MessageService.PostFromHub(m, Recipient.Founder, null,
                    $"{Wire.TaskId(task.Id)} \"{task.Title}\" has failed validation {row.Count} times and the conductor " +
                    $"has stopped restaffing it. Re-spec it, cancel it, or raise Muthur:ConductorMaxAttempts."
                    + Environment.NewLine + string.Join(Environment.NewLine, verdicts)
                    + Environment.NewLine + $"Full evidence: muthur task show {Wire.TaskId(task.Id)}", task.Id);
                m.Record("conductor.exhausted", task.Id, new { failures = row.Count });
                _lastAction = $"stopped restaffing {Wire.TaskId(task.Id)} after {row.Count} failures";
            }
        }, ct);
}

/// <summary>Runs conductor passes. Off unless the founder turned it on.</summary>
public sealed class ConductorWorker(IServiceProvider services, MuthurOptions options, TimeProvider clock, ILogger<ConductorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var started = await services.GetRequiredService<ConductorService>().RunPassAsync(stoppingToken);
                if (started > 0) logger.LogInformation("Conductor started {Count} validator session(s).", started);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Conductor pass failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, options.ConductorIntervalSeconds)), clock, stoppingToken);
        }
    }
}
