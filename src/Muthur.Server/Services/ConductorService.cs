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
public sealed class ConductorService(Ledger ledger, MuthurOptions options, IValidatorSessionLauncher launcher)
{
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly HashSet<string> _running = [];
    private readonly HashSet<int> _escalated = [];

    /// <summary>Sessions the conductor believes it has running, by "T-n/role".</summary>
    public int RunningCount { get { lock (_running) return _running.Count; } }

    public ConductorStatusDto Status() => new(
        options.ConductorEnabled,
        RunningCount,
        options.ConductorMaxSessions,
        options.ConductorSessionMinutes,
        options.ConductorMaxAttempts);

    /// <summary>The founder turns staffing on and off; the decision is in the ledger like any other.</summary>
    public async Task<ConductorStatusDto> SetEnabledAsync(Caller caller, bool enabled, CancellationToken ct = default)
    {
        var was = options.ConductorEnabled;
        options.ConductorEnabled = enabled;
        if (was != enabled)
            await ledger.MutateAsync(caller, m =>
            {
                m.Record(enabled ? "conductor.on" : "conductor.off", payload: new { maxSessions = options.ConductorMaxSessions });
                return Task.CompletedTask;
            }, ct);
        return Status();
    }

    /// <summary>
    /// What the conductor would staff right now, most urgent first. Separated from starting anything so the
    /// decision can be tested without launching a process.
    /// </summary>
    public Task<IReadOnlyList<ConductorAssignment>> PlanAsync(CancellationToken ct = default)
    {
        if (!options.ConductorEnabled) return Task.FromResult<IReadOnlyList<ConductorAssignment>>([]);

        return ledger.ReadAsync<IReadOnlyList<ConductorAssignment>>(async (db, now) =>
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
                    lock (_running)
                        if (_running.Contains($"{Wire.TaskId(task.Id)}/{validation.ValidatorKey}")) continue;

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
        if (!options.ConductorEnabled) return 0;
        if (!await _pass.WaitAsync(0, ct)) return 0;   // a slow pass must never overlap the next tick
        try
        {
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
        }
        catch (Exception ex)
        {
            await ledger.MutateAsync(Caller.Founder, m =>
            {
                m.Record("conductor.failed", assignment.TaskId, new { role = assignment.RoleKey, error = ex.Message });
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        finally
        {
            lock (_running) _running.Remove(key);
        }
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
                MessageService.PostFromHub(m, Recipient.Founder, null,
                    $"{Wire.TaskId(task.Id)} \"{task.Title}\" has failed validation {row.Count} times. The conductor " +
                    "has stopped restaffing it: re-spec it, cancel it, or raise the attempt ceiling.", task.Id);
                m.Record("conductor.exhausted", task.Id, new { failures = row.Count });
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
