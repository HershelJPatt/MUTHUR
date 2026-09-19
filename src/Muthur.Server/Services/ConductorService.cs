using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
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
/// One orchestrator session the conductor intends to start: a task in the backlog, and nobody on it yet.
/// <para>
/// No role, because an orchestrator takes none. The session claims the task itself, which is also the only thing
/// standing between two sessions and one task: the claim is the hub's, and the loser of that race exits.
/// </para>
/// </summary>
/// <param name="Resuming">
/// Whether this task has been worked on before. The backlog holds two kinds of task that look identical: work
/// nobody has begun, and work whose session went quiet and was swept back here with its spec and its branch
/// intact. A session told to start the second kind from nothing designs a task that is already designed.
/// </param>
/// <param name="PreviousOwner">Who last held it, out of the ledger, since the sweep cleared the column. Null if nothing says.</param>
public sealed record OrchestratorAssignment(
    int TaskId, string TaskKey, string TaskTitle, string Project, bool Resuming = false, string? PreviousOwner = null);

/// <summary>Starts one orchestrator session. Faked in tests; the real one launches a harness through <c>AgentLauncher</c>.</summary>
public interface IOrchestratorSessionLauncher
{
    Task StartAsync(OrchestratorAssignment assignment, CancellationToken ct = default);
}

/// <summary>
/// Staffs validation so a task that passes needs nobody awake.
/// <para>
/// The conductor adds no state to the task machine it serves. It only notices a task sitting in
/// <see cref="TaskState.Validating"/> whose required role has a free slot, and starts a session to hold it. A failed
/// task is already back with its owner; if that session has gone, the claim lapses and the task is staffable
/// again on a later pass — the bounce-back loop closes on machinery that already existed.
/// </para>
/// <para>
/// With the second switch on it also starts work: an orchestrator session for a task sitting in the backlog, which
/// claims the task itself. That half adds no state to the machine either — the backlog is where unstarted work
/// already lives, and a claim is how a task stops being unstarted — and validation is staffed first out of the one
/// shared ceiling, because a task in validation is closer to done than a task nobody has begun.
/// </para>
/// </summary>
public sealed class ConductorService(
    Ledger ledger,
    MuthurOptions options,
    TimeProvider clock,
    IValidatorSessionLauncher launcher,
    IOrchestratorSessionLauncher orchestrators)
{
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly HashSet<string> _running = [];

    /// <summary>
    /// The lifetime of every session this hub has started, cancelled as the host stops.
    /// <para>
    /// Sessions used to be launched with <c>CancellationToken.None</c>, which detached the child from the hub's
    /// lifetime on purpose. <see cref="_running"/> is in memory, so the next hub began with an empty set and
    /// staffed a task whose orchestrator was still alive: two sessions on one task, two specs, two branches — and
    /// a survivor that nothing can account for, because it is in no ceiling, no status line and no stall counter.
    /// <c>ProcessRunner</c> already kills the whole process tree when its token cancels; this is that token.
    /// </para>
    /// <para>
    /// Killing loses less than adopting would. The dead session's claim lapses, <c>LeaseSweeper</c> returns the
    /// task to the backlog with its branch and spec intact, and the next session continues from there. The work
    /// survives in the branch; only the process does not. Which is also why <see cref="_running"/> is still not
    /// persisted: with the children dead, an empty set after a restart is the truth.
    /// </para>
    /// </summary>
    private readonly CancellationTokenSource _sessions = new();

    /// <summary>
    /// The detached session tasks, so a stop can wait for the kills rather than race the host's exit. Pruned as
    /// sessions are started, so it stays the size of what is actually running.
    /// </summary>
    private readonly List<Task> _sessionTasks = [];

    /// <summary>
    /// How long a stop waits for the children to die. A hang detector, not a grace period — they are killed, not
    /// asked — and a stop that never returns is worse than a child that lingers a moment.
    /// </summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The role part of an orchestrator's <see cref="_running"/> and <see cref="_stalls"/> key. Both maps are keyed
    /// "T-n/role" and <see cref="PlanAsync"/> charges every running session to the role it names, so the two key
    /// spaces must not be able to meet. <c>RoleKey</c>'s pattern is <c>^[a-z0-9][a-z0-9-]{0,37}$</c>: a key can
    /// neither contain '#' nor begin with one, so nothing a founder can define reaches this name, and no
    /// orchestrator can be miscounted into a validator's slot.
    /// </summary>
    internal const string OrchestratorRole = "#orchestrator";

    /// <summary>Task id → the <see cref="CapState.RoundSeq"/> already announced, so a later round can announce again.</summary>
    private readonly Dictionary<int, long> _escalated = [];

    // F3: "what it last did" - a founder who turned this on overnight needs to know it ran at all.
    private DateTimeOffset? _lastPass;
    private string? _lastAction;

    /// <summary>
    /// Consecutive unproductive sessions, per task and role, and when that pair last gave up.
    /// <para>
    /// A validator that ran and said no is progress; a session that could not start, one that started and never
    /// finished, and one that ran to a clean exit having recorded nothing are all the organization wedged, and
    /// retrying any of them every interval all night writes thousands of ledger events while `status` reads
    /// perfectly healthy. So a pair stops after <c>ConductorMaxAttempts</c> — but it stops <em>half-open</em>:
    /// one probe is let through every <c>ConductorStallProbeMinutes</c>. Whatever was wrong is usually fixed from
    /// outside the hub — a quota cleared, a harness installed, a repository remounted, a spec rewritten — and the
    /// founder should not have to know an incantation to resume. A probe that reaches a verdict clears the count;
    /// one that does not re-arms the cooldown without saying anything further.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, Stall> _stalls = [];

    /// <summary>Why a session produced nothing. The founder is sent to a different place for each.</summary>
    private enum Unproductive { NeverStarted, RanAndFailed, NoVerdict }

    /// <summary>What a session that exited cleanly actually left behind.</summary>
    private enum RoundEnd
    {
        /// <summary>This pair recorded a verdict. The pair is working.</summary>
        Verdict,
        /// <summary>Another validator decided the task first, so there was nothing left for this pair to record.</summary>
        Closed,
        /// <summary>The round is still open and this pair recorded nothing. That is the defect the cap catches.</summary>
        Nothing,
    }

    private sealed record Stall(int Failures, DateTimeOffset? StalledAt, bool Announced, Unproductive Last);

    /// <param name="Failures">Times this task has ever been failed — the ledger is append-only, so this only rises.</param>
    /// <param name="RoundSeq">Seq of the most recent <c>task.implemented</c>: the round this task is in now.</param>
    /// <param name="HeadMoved">Whether that round's branch head differs from the round before it.</param>
    private sealed record CapState(int Failures, long RoundSeq, bool HeadMoved);

    /// <summary>
    /// Whether staffing is on. The hub is a local process that is off for hours, so this lives in the database:
    /// a founder who turned it on must not find it silently off after the next restart.
    /// </summary>
    private Task<bool> EnabledAsync(CancellationToken ct) =>
        ledger.ReadAsync(async (db, _) =>
            await db.Meta.Where(e => e.Key == MetaEntry.ConductorEnabled).Select(e => e.Value).SingleOrDefaultAsync(ct)
                is { } stored ? stored == "true" : options.ConductorEnabled, ct);

    /// <summary>
    /// Whether the conductor may also start orchestrators. Absent means no — there is deliberately no
    /// configuration default to fall back on, so a hub that upgrades into this build spends nothing new until the
    /// founder says so, and says so in the database where a restart cannot quietly take it back.
    /// </summary>
    private Task<bool> OrchestratorsEnabledAsync(CancellationToken ct) =>
        ledger.ReadAsync(async (db, _) =>
            await db.Meta.Where(e => e.Key == MetaEntry.ConductorOrchestrators).Select(e => e.Value).SingleOrDefaultAsync(ct) == "true", ct);

    /// <summary>The founder turns the orchestrator half on and off; the decision is in the ledger like any other.</summary>
    public async Task<ConductorStatusDto> SetOrchestratorsAsync(Caller caller, bool enabled, CancellationToken ct = default)
    {
        var was = await OrchestratorsEnabledAsync(ct);
        await ledger.MutateAsync(caller, async m =>
        {
            var row = await m.Db.Meta.SingleOrDefaultAsync(e => e.Key == MetaEntry.ConductorOrchestrators, ct);
            if (row is null) m.Db.Meta.Add(new MetaEntry { Key = MetaEntry.ConductorOrchestrators, Value = enabled ? "true" : "false" });
            else row.Value = enabled ? "true" : "false";
            if (was != enabled) m.Record(enabled ? "conductor.orchestrators_on" : "conductor.orchestrators_off");
        }, ct);
        return await StatusAsync(ct);
    }

    /// <summary>Sessions the conductor believes it has running, by "T-n/role".</summary>
    public int RunningCount { get { lock (_running) return _running.Count; } }

    /// <summary>Remember a detached session so a stop can wait for it, dropping the ones that have already ended.</summary>
    private void Track(Task session)
    {
        lock (_running)
        {
            _sessionTasks.RemoveAll(t => t.IsCompleted);
            _sessionTasks.Add(session);
        }
    }

    /// <summary>
    /// End every session this hub started, and say so once. Called as the host stops and before it exits, because a
    /// child that outlives the hub belongs to nobody: the next hub starts with an empty <see cref="_running"/> and
    /// staffs its task all over again.
    /// <para>
    /// Idempotent, and silent when there was nothing to end — a founder should read
    /// <c>conductor.sessions_terminated</c> as "work was cut off here", not as a line every shutdown prints.
    /// </para>
    /// </summary>
    public async Task StopSessionsAsync(CancellationToken ct = default)
    {
        if (_sessions.IsCancellationRequested) return;

        Task[] running;
        int count;
        lock (_running)
        {
            count = _running.Count;
            running = [.. _sessionTasks.Where(t => !t.IsCompleted)];
        }

        await _sessions.CancelAsync();

        // The sessions observe the cancellation on a pool thread and kill their process trees there, so wait for
        // them instead of racing the host's exit — otherwise the event below records an intention, not an outcome.
        if (running.Length > 0)
        {
            try { await Task.WhenAll(running).WaitAsync(StopBudget, CancellationToken.None); }
            catch (TimeoutException) { }
        }

        if (count == 0) return;
        await ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record("conductor.sessions_terminated", payload: new { count });
            return Task.CompletedTask;
        }, ct);
    }

    public async Task<ConductorStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var ceiling = await CeilingAsync(ct);
        return new(
            await EnabledAsync(ct),
            RunningCount,
            options.ConductorMaxSessions,
            options.ConductorSessionMinutes,
            options.ConductorMaxAttempts,
            options.EffectiveConductorIntervalSeconds,   // what it runs at, not what was asked for
            options.ConductorStallProbeMinutes,
            _lastPass,
            _lastAction,
            ceiling.Sessions,
            ceiling.Reason,
            await OrchestratorsEnabledAsync(ct));
    }

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
            if (enabled) lock (_stalls) _stalls.Clear();
        }, ct);
        return await StatusAsync(ct);
    }

    /// <summary>An unattended window in local time, and the ceiling it holds the conductor to while it is open.</summary>
    private sealed record Window(TimeSpan From, TimeSpan To, int Sessions);

    /// <summary>
    /// How many sessions a pass may run, and the one sentence that says where the number came from.
    /// <para>
    /// The founder moves this while the hub runs, so it lives in the database beside the on/off switch and for the
    /// same reason: a restart must not quietly hand the configuration file back its say. Configuration is the
    /// default, not the authority.
    /// </para>
    /// </summary>
    private Task<(int Sessions, string Reason)> CeilingAsync(CancellationToken ct) =>
        ledger.ReadAsync(async (db, now) =>
        {
            var stored = await db.Meta
                .Where(e => e.Key == MetaEntry.ConductorSessions || e.Key == MetaEntry.ConductorUnattended)
                .ToDictionaryAsync(e => e.Key, e => e.Value, ct);
            return Ceiling(
                stored.GetValueOrDefault(MetaEntry.ConductorSessions),
                stored.GetValueOrDefault(MetaEntry.ConductorUnattended),
                // Local time, because "while I am asleep" is a fact about the founder's night and not about UTC.
                TimeZoneInfo.ConvertTime(now, clock.LocalTimeZone).TimeOfDay);
        }, ct);

    private (int Sessions, string Reason) Ceiling(string? sessions, string? unattended, TimeSpan localNow)
    {
        var (configured, reason) = int.TryParse(sessions, CultureInfo.InvariantCulture, out var set) && set >= 1
            ? (set, "Set by the founder.")
            : (options.ConductorMaxSessions, "Muthur:ConductorMaxSessions.");

        if (ParseWindow(unattended) is not { } window || !IsInside(window, localNow)) return (configured, reason);
        var capped = Math.Min(configured, window.Sessions);
        return (capped, $"Unattended {window.From:hh\\:mm}-{window.To:hh\\:mm} caps this at {capped}.");
    }

    /// <summary>"22:00-07:00@1". Written only by <see cref="SetCeilingAsync"/>, so anything else is read as no window.</summary>
    private static Window? ParseWindow(string? stored)
    {
        if (stored is not { Length: > 0 }) return null;
        var at = stored.LastIndexOf('@');
        var dash = stored.IndexOf('-');
        if (dash < 0 || at < dash) return null;
        if (ParseTime(stored[..dash]) is not { } from || ParseTime(stored[(dash + 1)..at]) is not { } to) return null;
        if (!int.TryParse(stored[(at + 1)..], CultureInfo.InvariantCulture, out var sessions) || sessions < 1) return null;
        return from == to ? null : new Window(from, to, sessions);   // a window with no width is not a window
    }

    /// <summary>A window runs from its start to its end, and one that ends before it starts has crossed midnight.</summary>
    private static bool IsInside(Window window, TimeSpan localNow) =>
        window.From < window.To
            ? localNow >= window.From && localNow < window.To
            : localNow >= window.From || localNow < window.To;

    /// <summary>"HH:mm", 24-hour, exactly as the founder is told. Null means that is not a time.</summary>
    private static TimeSpan? ParseTime(string? text)
    {
        if (text is not { Length: 5 } || text[2] != ':') return null;
        if (!char.IsAsciiDigit(text[0]) || !char.IsAsciiDigit(text[1]) ||
            !char.IsAsciiDigit(text[3]) || !char.IsAsciiDigit(text[4])) return null;
        var hours = ((text[0] - '0') * 10) + (text[1] - '0');
        var minutes = ((text[3] - '0') * 10) + (text[4] - '0');
        return hours <= 23 && minutes <= 59 ? new TimeSpan(hours, minutes, 0) : null;
    }

    private static MuthurException SessionsInvalid() =>
        Fail.Rule("sessions_invalid", "The conductor needs at least one session to do anything.");

    private static MuthurException TimeInvalid() => Fail.Rule("time_invalid", "Times are HH:mm, 24-hour.");

    /// <summary>
    /// The founder raises or lowers the ceiling, or caps the hours nobody is watching. Recorded in the ledger like
    /// every other decision, and kept in the database so the next restart still knows about it.
    /// </summary>
    public async Task<ConductorStatusDto> SetCeilingAsync(Caller caller, ConductorSessionsRequest request, CancellationToken ct = default)
    {
        if (request.Sessions is < 1 || request.UnattendedSessions is < 1) throw SessionsInvalid();

        var given = new[] { request.UnattendedFrom is { Length: > 0 }, request.UnattendedTo is { Length: > 0 }, request.UnattendedSessions is not null };
        if (given.Any(g => g) && !given.All(g => g))
            throw Fail.Rule("unattended_incomplete", "An unattended window needs --from, --to and --sessions.");

        // Both times before anything is written: half a window is worse than none.
        var window = given[0]
            ? new Window(ParseTime(request.UnattendedFrom) ?? throw TimeInvalid(),
                ParseTime(request.UnattendedTo) ?? throw TimeInvalid(), request.UnattendedSessions!.Value)
            : null;

        await ledger.MutateAsync(caller, async m =>
        {
            async Task StoreAsync(string key, string? value)
            {
                var row = await m.Db.Meta.SingleOrDefaultAsync(e => e.Key == key, ct);
                if (value is null) { if (row is not null) m.Db.Meta.Remove(row); }
                else if (row is null) m.Db.Meta.Add(new MetaEntry { Key = key, Value = value });
                else row.Value = value;
            }

            if (request.Clear)
            {
                await StoreAsync(MetaEntry.ConductorSessions, null);
                await StoreAsync(MetaEntry.ConductorUnattended, null);
                m.Record("conductor.ceiling_cleared");
                return;
            }

            if (request.Sessions is { } sessions)
            {
                await StoreAsync(MetaEntry.ConductorSessions, sessions.ToString(CultureInfo.InvariantCulture));
                m.Record("conductor.sessions_set", payload: new { sessions });
            }

            if (window is not null)
            {
                var (from, to) = ($"{window.From:hh\\:mm}", $"{window.To:hh\\:mm}");
                await StoreAsync(MetaEntry.ConductorUnattended, $"{from}-{to}@{window.Sessions}");
                m.Record("conductor.unattended_set", payload: new { from, to, sessions = window.Sessions });
            }
        }, ct);

        return await StatusAsync(ct);
    }

    /// <summary>
    /// What both cap sites ask of a task, in one query so they cannot drift: how often it has been failed, and
    /// whether the round it is in now is standing on the same commit as the round before it.
    /// <para>
    /// A round can hold at most one failure — a failed verdict returns the task to its owner, and only a fresh
    /// <c>task.implemented</c> brings it back — so "failed three times" has always meant three resubmissions.
    /// The cap is therefore not "has it failed a lot" but "has it failed a lot and come back unchanged".
    /// </para>
    /// </summary>
    private static async Task<Dictionary<int, CapState>> CapStateAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct)
    {
        var failures = await db.Events
            .Where(e => e.Type == "validation.failed" && e.TaskId != null && taskIds.Contains(e.TaskId!.Value))
            .GroupBy(e => e.TaskId!.Value)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        // The heads live in the payload, and the validating set is small: read them in memory rather than
        // teaching the query about JSON.
        var rounds = await db.Events
            .Where(e => e.Type == "task.implemented" && e.TaskId != null && taskIds.Contains(e.TaskId!.Value))
            .Select(e => new { TaskId = e.TaskId!.Value, e.Seq, e.PayloadJson })
            .ToListAsync(ct);

        var state = new Dictionary<int, CapState>();
        foreach (var perTask in rounds.GroupBy(e => e.TaskId))
        {
            var newest = perTask.OrderByDescending(e => e.Seq).Take(2).ToList();
            var current = Head(newest[0].PayloadJson);
            var previous = newest.Count > 1 ? Head(newest[1].PayloadJson) : null;
            // Absence of evidence is not evidence of standing still: rounds recorded before heads were kept
            // carry none, and a task must never be stalled for something nobody wrote down.
            state[perTask.Key] = new CapState(
                failures.GetValueOrDefault(perTask.Key), newest[0].Seq,
                current is null || previous is null || current != previous);
        }
        return state;
    }

    private static string? Head(string payloadJson) => Text(payloadJson, "head");

    /// <summary>One string out of an event payload. A payload that is not what was hoped for says nothing rather than throwing.</summary>
    private static string? Text(string payloadJson, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
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

            // Free slots, not "is it held at all": a validator role is a skill several agents may hold at once.
            var holds = await db.RoleHolds.Include(h => h.Agent).Where(h => h.LeaseExpires > now).ToListAsync(ct);
            var liveHolders = holds.GroupBy(h => h.RoleKey).ToDictionary(g => g.Key, g => g.Count());
            var capacities = await db.Roles.Where(r => r.IsValidator).ToDictionaryAsync(r => r.Key, r => r.Holders, ct);

            // A slot is taken by a hold or by a session already on its way to one. A session that has started but
            // has not yet taken the role holds nothing yet, and without this the next pass plans straight over it:
            // the extras start, fail to take the role, and produce nothing. Read once, never per candidate.
            //
            // Once that session does take the role it is both a hold and a running session, and counting it in
            // both places charges one validator two slots — a capacity of two would then staff a second task
            // only while the first session was still starting up, and serialize for the rest of its life. The
            // session's agent name is the (task, role) pair's own, so a hold by that name is that session: it is
            // already counted, and the running set must not count it again.
            var heldBy = holds.Select(h => (h.RoleKey, Name: h.Agent?.Name ?? "")).ToHashSet();
            lock (_running)
                foreach (var session in _running)
                {
                    var slash = session.IndexOf('/');                   // keys are "T-n/role"
                    var (task, role) = (session[..slash], session[(slash + 1)..]);
                    // Not every running session is a validator. An orchestrator's key is "T-n/#orchestrator" and it
                    // holds no role at all, so nothing here is its slot to take. Skipping it keeps that fact local
                    // rather than resting on the reader knowing that no role key the hub accepts can begin with a
                    // '#', which is what would otherwise have to hold for a validator never to be starved by one.
                    if (role.StartsWith('#')) continue;
                    if (heldBy.Contains((role, ValidatorSessionLauncher.IdentityName(task, role)))) continue;
                    liveHolders[role] = liveHolders.GetValueOrDefault(role) + 1;
                }

            // A pair a validator has already taken is not the conductor's to staff.
            var claimed = pending.Where(v => LeasePolicy.IsClaimLive(v, now))
                .Select(v => (v.TaskId, v.ValidatorKey)).ToHashSet();

            // A task that has failed too many times and come back unchanged is a judgment call, and judgment
            // calls go to the founder.
            var cap = await CapStateAsync(db, ids, ct);

            // "harness/model" of whoever declared the task implemented.
            var built = await db.Events
                .Where(e => e.Type == "task.implemented" && e.TaskId != null && ids.Contains(e.TaskId!.Value) && e.ActorModel != null)
                .GroupBy(e => e.TaskId!.Value)
                .Select(g => new { TaskId = g.Key, Model = g.OrderByDescending(e => e.Seq).First().ActorModel })
                .ToDictionaryAsync(x => x.TaskId, x => x.Model, ct);

            var plan = new List<ConductorAssignment>();
            foreach (var task in tasks)
            {
                if (cap.GetValueOrDefault(task.Id) is { HeadMoved: false } state &&
                    state.Failures >= options.ConductorMaxAttempts) continue;
                // A human has said this one needs them. Staffing it spends a session to be told what the task already says.
                if (task.AttendedReason is not null) continue;
                foreach (var validation in pending.Where(v => v.TaskId == task.Id))
                {
                    if (claimed.Contains((task.Id, validation.ValidatorKey))) continue;   // someone is already on it
                    if (!capacities.TryGetValue(validation.ValidatorKey, out var capacity)) continue;   // a role that no longer exists
                    // A session that cannot take the role is a session that does nothing.
                    if (liveHolders.GetValueOrDefault(validation.ValidatorKey) >= capacity) continue;
                    var key = $"{Wire.TaskId(task.Id)}/{validation.ValidatorKey}";
                    lock (_running)
                        if (_running.Contains(key)) continue;
                    lock (_stalls)
                        if (_stalls.GetValueOrDefault(key) is { StalledAt: { } stalled } &&
                            stalled + TimeSpan.FromMinutes(options.ConductorStallProbeMinutes) > now)
                            continue;

                    plan.Add(new ConductorAssignment(
                        task.Id, Wire.TaskId(task.Id), task.Title, task.Project?.Key ?? "",
                        validation.ValidatorKey,
                        built.GetValueOrDefault(task.Id) is { Length: > 0 } model ? model.Split('/')[0] : null));

                    // The session this plan asks for will take a slot, so the rest of the pass sees one fewer:
                    // otherwise three waiting tasks become three sessions for a role with two free slots.
                    liveHolders[validation.ValidatorKey] = liveHolders.GetValueOrDefault(validation.ValidatorKey) + 1;
                }
            }
            return plan;
        }, ct);
    }

    /// <summary>The <see cref="_running"/> and <see cref="_stalls"/> key for one task's orchestrator.</summary>
    private static string OrchestratorKey(string taskKey) => $"{taskKey}/{OrchestratorRole}";

    /// <summary>
    /// Whether this task has been worked on before. <c>task spec</c> requires an owner and a branch is recorded
    /// only by <c>task implemented</c>, so either one means some session has already had this task — and the sweep
    /// that returned it to the backlog kept both while clearing the owner.
    /// </summary>
    private static bool CarriesWork(WorkTask task) =>
        task.SpecPath is { Length: > 0 } || task.Branch is { Length: > 0 };

    /// <summary>
    /// Who last held each of these tasks, read out of the ledger because the column is gone: the sweep that
    /// returned the task to the backlog cleared its owner. <c>task.claimed</c> names the agent that took it and
    /// <c>task.claim_expired</c> names the agent it lapsed from, so the newer of the two is whoever had it last.
    /// <c>task.released</c> is deliberately not one of them: it names the caller, and the caller is usually the
    /// founder releasing somebody else's task, which would put the wrong name in front of the next session.
    /// </summary>
    private static async Task<Dictionary<int, string>> PreviousOwnersAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];

        var held = await db.Events
            .Where(e => (e.Type == "task.claimed" || e.Type == "task.claim_expired") &&
                        e.TaskId != null && taskIds.Contains(e.TaskId!.Value))
            .GroupBy(e => e.TaskId!.Value)
            .Select(g => new { TaskId = g.Key, Payload = g.OrderByDescending(e => e.Seq).First().PayloadJson })
            .ToDictionaryAsync(x => x.TaskId, x => x.Payload, ct);

        var owners = new Dictionary<int, string>();
        foreach (var (taskId, payload) in held)
            if (Text(payload, "agent") is { Length: > 0 } agent) owners[taskId] = agent;
        return owners;
    }

    /// <summary>
    /// What the conductor would start work on right now, most urgent first. Separated from starting anything for
    /// the same reason <see cref="PlanAsync"/> is: the decision has to be testable without spending a session.
    /// <para>
    /// Backlog only, and that one word carries most of the safety. A task somebody has claimed is
    /// <see cref="TaskState.InProgress"/>, a task waiting on the founder is <see cref="TaskState.Blocked"/>, and a
    /// task under validation is somebody else's pass — none of them is work nobody has started.
    /// </para>
    /// <para>
    /// What the backlog is not is only work nobody has begun. A task whose owner goes quiet is returned here by
    /// <c>TaskService.SweepExpiredClaimsAsync</c> within half a minute, keeping its spec, its branch and its
    /// priority and losing only its owner — so "never started" and "half built and abandoned" arrive looking
    /// exactly alike. What is already attached to the task is the difference, and a session that is not told
    /// specs the work a second time and builds over a branch it never read.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<OrchestratorAssignment>> PlanOrchestratorsAsync(CancellationToken ct = default)
    {
        // Two switches, both of which must be on. The main one is the conductor as a whole; the second is the half
        // that begins new work, and it is off until the founder has said otherwise on this hub.
        if (!await EnabledAsync(ct) || !await OrchestratorsEnabledAsync(ct)) return [];

        return await ledger.ReadAsync<IReadOnlyList<OrchestratorAssignment>>(async (db, now) =>
        {
            // A project with no repository on record has nowhere for a session to run, and starting one there
            // spends a subscription to find that out.
            var waiting = await db.Tasks.Include(t => t.Project)
                .Where(t => t.State == TaskState.Backlog && t.Project != null && t.Project.RepoPath != "")
                .ToListAsync(ct);
            if (waiting.Count == 0) return [];

            // The founder's priority first and above everything else: it is the only lever they have for saying
            // "this one matters most", and a queue that answers an urgent task by starting an old one instead is
            // not a queue they can steer. Only among tasks they ranked equally does half-built work go first —
            // finishing what is begun beats starting something new, but it never overrules what was called urgent.
            var tasks = waiting
                .OrderByDescending(t => t.Priority)
                .ThenByDescending(CarriesWork)
                .ThenBy(t => t.Id)
                .ToList();
            var owners = await PreviousOwnersAsync(db, [.. tasks.Where(CarriesWork).Select(t => t.Id)], ct);

            var plan = new List<OrchestratorAssignment>();
            foreach (var task in tasks)
            {
                var key = OrchestratorKey(Wire.TaskId(task.Id));
                lock (_running)
                    if (_running.Contains(key)) continue;
                lock (_stalls)
                    if (_stalls.GetValueOrDefault(key) is { StalledAt: { } stalled } &&
                        stalled + TimeSpan.FromMinutes(options.ConductorStallProbeMinutes) > now)
                        continue;

                var resuming = CarriesWork(task);
                plan.Add(new OrchestratorAssignment(task.Id, Wire.TaskId(task.Id), task.Title, task.Project?.Key ?? "",
                    resuming, resuming ? owners.GetValueOrDefault(task.Id) : null));
            }
            return plan;
        }, ct);
    }

    /// <summary>One pass: start what the plan asks for, up to the session budget. Returns how many it started.</summary>
    public async Task<int> RunPassAsync(CancellationToken ct = default)
    {
        // A pass that races the stop would launch a child with nobody left to cancel it — the orphan this whole
        // mechanism exists to prevent, created by the mechanism's own shutdown.
        if (_sessions.IsCancellationRequested) return 0;
        if (!await EnabledAsync(ct)) return 0;
        if (!await _pass.WaitAsync(0, ct)) return 0;   // a slow pass must never overlap the next tick
        try
        {
            _lastPass = clock.GetUtcNow();
            await EscalateExhaustedAsync(ct);

            // Read once per pass: the founder may move it mid-pass, and a ceiling that changes under the loop
            // would let a pass start more sessions than either number allows.
            var ceiling = (await CeilingAsync(ct)).Sessions;

            var started = 0;
            foreach (var assignment in await PlanAsync(ct))
            {
                var key = $"{assignment.TaskKey}/{assignment.RoleKey}";
                lock (_running)
                {
                    if (_running.Count >= ceiling) break;
                    if (!_running.Add(key)) continue;
                }

                await ledger.MutateAsync(Caller.Founder, m =>
                {
                    m.Record("conductor.staffing", assignment.TaskId,
                        new { role = assignment.RoleKey, avoidHarness = assignment.AvoidHarness });
                    return Task.CompletedTask;
                }, ct);

                _lastAction = $"staffed {assignment.TaskKey} for {assignment.RoleKey}";
                Track(RunSessionAsync(assignment, key));
                started++;
            }

            // Validation first, and only then new work, against the one ceiling both halves share. A task in
            // validating is closer to done than a task in the backlog, so a full ceiling drains what is nearly
            // finished rather than starting more of what will have to be validated later.
            foreach (var assignment in await PlanOrchestratorsAsync(ct))
            {
                var key = OrchestratorKey(assignment.TaskKey);
                lock (_running)
                {
                    if (_running.Count >= ceiling) break;
                    if (!_running.Add(key)) continue;
                }

                await ledger.MutateAsync(Caller.Founder, m =>
                {
                    m.Record("conductor.staffing", assignment.TaskId, new { role = OrchestratorRole });
                    return Task.CompletedTask;
                }, ct);

                _lastAction = $"staffed an orchestrator for {assignment.TaskKey}";
                Track(RunOrchestratorSessionAsync(assignment, key));
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
            Exception? failure = null;
            try { await launcher.StartAsync(assignment, _sessions.Token); }
            catch (Exception thrown) { failure = thrown; }

            // The hub is stopping and killed this session with its process tree. Nothing about that is the pair's
            // doing, and charging it would leave every session in flight one strike worse off for a restart —
            // three restarts would stall work that was never given the chance to fail.
            if (_sessions.IsCancellationRequested) return;

            // A session that ran and hung is not a session that never started, and the founder must not be sent
            // looking for a missing CLI when the real fault is sessions outliving their timeout.
            //
            // Classified on what is known, never on the absence of one type: an exception nobody anticipated is
            // far likelier to mean the session never began than that it ran, and this wording is the only thing
            // telling the founder where to look.
            if (failure is { } ex)
            {
                await UnproductiveAsync(assignment, key,
                    ex is ValidatorSessionException ? Unproductive.RanAndFailed : Unproductive.NeverStarted, ex.Message);
            }
            else switch (await RoundEndAsync(assignment))
            {
                case RoundEnd.Verdict:
                    lock (_stalls) _stalls.Remove(key);   // including a probe: the pair is working again
                    break;
                case RoundEnd.Closed:
                    // Another validator decided the task while this session was running, so there was nothing left
                    // to record. Not charged, because the session did nothing wrong. Not cleared either: a pair that
                    // only ever runs in rounds closed by others must not be laundered clean without ever reaching a
                    // verdict. The event is recorded rather than swallowed because the session still spent a slot.
                    await ledger.MutateAsync(Caller.Founder, m =>
                    {
                        m.Record("conductor.round_closed", assignment.TaskId, new { role = assignment.RoleKey });
                        return Task.CompletedTask;
                    }, CancellationToken.None);
                    break;
                default:
                    // It started, it exited cleanly, and it recorded nothing. That is not progress: it is the loop that
                    // spent five harness sessions in twenty-eight minutes on a task no unattended validator could do.
                    await UnproductiveAsync(assignment, key, Unproductive.NoVerdict, null);
                    break;
            }
        }
        finally
        {
            lock (_running) _running.Remove(key);
        }
    }

    /// <summary>
    /// One orchestrator session, from start to whatever it left behind.
    /// <para>
    /// The same three endings a validator has, read off the one thing an orchestrator was started to do. It threw
    /// before a process existed, or a process ran and died — those are the launch/session split validators already
    /// make. Otherwise it exited cleanly, and the question is whether the task moved: a session that ran its whole
    /// timeout and left the task sitting in the backlog claimed nothing, and restarting it every interval spends
    /// three quarters of an hour a time to achieve exactly that again. So it is charged like a validator that
    /// reached no verdict, on the same counter and the same half-open cooldown.
    /// </para>
    /// </summary>
    private async Task RunOrchestratorSessionAsync(OrchestratorAssignment assignment, string key)
    {
        // The stall machinery speaks in (task, role) pairs. An orchestrator is one more pair, under the role name
        // no role key can spell.
        var pair = new ConductorAssignment(assignment.TaskId, assignment.TaskKey, assignment.TaskTitle,
            assignment.Project, OrchestratorRole, AvoidHarness: null);
        try
        {
            Exception? failure = null;
            try { await orchestrators.StartAsync(assignment, _sessions.Token); }
            catch (Exception thrown) { failure = thrown; }

            // The hub stopped it; see RunSessionAsync. The claim lapses, the sweeper returns the task to the
            // backlog with its branch, and the next hub staffs it once — which is the whole point of killing it.
            if (_sessions.IsCancellationRequested) return;

            if (failure is { } ex)
            {
                await UnproductiveAsync(pair, key,
                    ex is ValidatorSessionException ? Unproductive.RanAndFailed : Unproductive.NeverStarted, ex.Message);
            }
            else if (await StillInBacklogAsync(assignment.TaskId))
            {
                await UnproductiveAsync(pair, key, Unproductive.NoVerdict, null);
            }
            else
            {
                lock (_stalls) _stalls.Remove(key);   // the task left the backlog: the session did what it was for
            }
        }
        finally
        {
            lock (_running) _running.Remove(key);
        }
    }

    /// <summary>
    /// Whether the task is still where the plan found it. Anything else — claimed, blocked, cancelled, landed — is
    /// a task that moved, and the session that was started for it is not charged for the move it did not make.
    /// </summary>
    private Task<bool> StillInBacklogAsync(int taskId) =>
        ledger.ReadAsync((db, _) =>
            db.Tasks.AnyAsync(t => t.Id == taskId && t.State == TaskState.Backlog, CancellationToken.None), CancellationToken.None);

    /// <summary>
    /// What a session that exited cleanly actually left behind. A pair's own row is not enough to tell: sessions are
    /// started one per pending validation and run concurrently, so the first validator to fail a task takes it out of
    /// <see cref="TaskState.Validating"/> and leaves every sibling's row <see cref="Verdict.Pending"/> with nothing
    /// left for it to record.
    /// </summary>
    private Task<RoundEnd> RoundEndAsync(ConductorAssignment assignment) =>
        ledger.ReadAsync(async (db, _) =>
        {
            var round = await db.Tasks
                .Where(t => t.Id == assignment.TaskId)
                .Select(t => new
                {
                    t.State,
                    Pending = db.TaskValidations.Any(v =>
                        v.TaskId == t.Id && v.ValidatorKey == assignment.RoleKey && v.Verdict == Verdict.Pending),
                })
                .SingleOrDefaultAsync(CancellationToken.None);

            // The verdict first, and only then the task's state. A task that reached Validated because every
            // validator passed is not Validating either, and reading those pairs as closed rounds would credit
            // each of them with having done nothing. A row that is gone — the next round wiped it, because
            // ImplementedAsync removes the previous round's rows — is not pending, so it is not the cap's business.
            if (round is not { Pending: true }) return RoundEnd.Verdict;
            return round.State == TaskState.Validating ? RoundEnd.Nothing : RoundEnd.Closed;
        }, CancellationToken.None);

    /// <summary>
    /// What a session of this pair's kind is called, singular and plural. The founder reads these at three in the
    /// morning off a card, and the noun is what tells them which half of the conductor has stopped: being told a
    /// validator could not start, for a task no validator has been asked to look at yet, sends them to the wrong
    /// place entirely.
    /// </summary>
    private static (string One, string Many) Noun(string role) =>
        role == OrchestratorRole ? ("an orchestrator", "orchestrators") : ("a validator", "validators");

    /// <summary>
    /// One session produced nothing: count it, and when the pair has run out of attempts stall it half-open and tell
    /// the founder once, in the words that send them to the right place for this kind of nothing.
    /// </summary>
    private Task UnproductiveAsync(ConductorAssignment assignment, string key, Unproductive outcome, string? error)
    {
        Stall stall;
        lock (_stalls)
        {
            var previous = _stalls.GetValueOrDefault(key);
            var failures = previous is null ? 1 : previous.Failures + 1;
            var stalling = failures >= options.ConductorMaxAttempts;
            stall = new Stall(failures, stalling ? clock.GetUtcNow() : null, previous?.Announced == true, outcome);
            _stalls[key] = stall with { Announced = stall.Announced || stalling };
        }

        return ledger.MutateAsync(Caller.Founder, m =>
        {
            m.Record(outcome switch
            {
                Unproductive.RanAndFailed => "conductor.session_failed",
                Unproductive.NoVerdict => "conductor.no_verdict",
                _ => "conductor.failed",
            }, assignment.TaskId, outcome == Unproductive.NoVerdict
                ? new { role = assignment.RoleKey, attempt = stall.Failures }
                : (object)new { role = assignment.RoleKey, error, attempt = stall.Failures });

            if (stall.StalledAt is null || stall.Announced) return Task.CompletedTask;

            // Stop rather than degrade, and say so once: the founder's only other signal is that nothing shipped.
            m.Record("conductor.stalled", assignment.TaskId,
                new { role = assignment.RoleKey, error, ran = outcome != Unproductive.NeverStarted });
            var (one, many) = Noun(assignment.RoleKey);
            MessageService.PostFromHub(m, Recipient.Founder, null,
                (outcome switch
                {
                    Unproductive.RanAndFailed =>
                        $"The conductor started {one} for {assignment.TaskKey} ({assignment.RoleKey}) " +
                        $"{stall.Failures} times and none of them finished: {error}",
                    Unproductive.NoVerdict when assignment.RoleKey == OrchestratorRole =>
                        $"The conductor started {stall.Failures} {many} for {assignment.TaskKey} ({assignment.RoleKey}) " +
                        "and none of them claimed it. They ran and exited cleanly, so something is stopping them from " +
                        "starting the task at all rather than failing at it. Look at the bus for what they said, then " +
                        "re-spec the task, take it yourself, or raise Muthur:ConductorMaxAttempts.",
                    Unproductive.NoVerdict =>
                        $"The conductor started {stall.Failures} {many} for {assignment.TaskKey} ({assignment.RoleKey}) " +
                        "and none of them reached a verdict. They ran and exited cleanly, so something is stopping them " +
                        "from validating at all rather than failing. Look at the bus for what they said, then re-spec " +
                        "the task, validate it yourself, or raise Muthur:ConductorMaxAttempts.",
                    _ =>
                        $"The conductor could not start {one} for {assignment.TaskKey} ({assignment.RoleKey}) " +
                        $"{stall.Failures} times and has stopped trying: {error}",
                }) + Environment.NewLine +
                $"Fix the cause and it retries by itself within {options.ConductorStallProbeMinutes} " +
                (options.ConductorStallProbeMinutes == 1 ? "minute; " : "minutes; ") +
                "`muthur conductor off --founder && muthur conductor on --founder` retries at once.",
                assignment.TaskId);
            _lastAction = outcome switch
            {
                Unproductive.RanAndFailed => $"gave up running {assignment.RoleKey} for {assignment.TaskKey}: {error}",
                Unproductive.NoVerdict => $"gave up on {assignment.RoleKey} for {assignment.TaskKey}: {stall.Failures} sessions, no verdict",
                _ => $"gave up starting {assignment.RoleKey} for {assignment.TaskKey}: {error}",
            };
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// A task that keeps failing and keeps coming back on the same commit stops being restaffed and becomes a
    /// question for the founder, once per round — a later unmoved resubmission is new information and says so again.
    /// </summary>
    private Task EscalateExhaustedAsync(CancellationToken ct) =>
        ledger.MutateAsync(Caller.Founder, async m =>
        {
            var validating = await m.Db.Tasks.Where(t => t.State == TaskState.Validating).Select(t => t.Id).ToListAsync(ct);
            if (validating.Count == 0) return;

            var exhausted = (await CapStateAsync(m.Db, validating, ct))
                .Where(e => e.Value.Failures >= options.ConductorMaxAttempts && !e.Value.HeadMoved)
                .OrderBy(e => e.Key);

            foreach (var (taskId, state) in exhausted)
            {
                lock (_escalated)
                {
                    if (_escalated.TryGetValue(taskId, out var announced) && announced == state.RoundSeq) continue;
                    _escalated[taskId] = state.RoundSeq;
                }

                var task = await m.Db.Tasks.FirstAsync(t => t.Id == taskId, ct);
                var events = await m.Db.Events
                    .Where(e => e.Type == "validation.failed" && e.TaskId == task.Id)
                    .OrderBy(e => e.Seq).Select(e => new { e.At, e.Actor, e.PayloadJson }).ToListAsync(ct);
                var verdicts = events.Select(e => $"- {e.At:yyyy-MM-dd HH:mm} {e.Actor}: {Evidence.FirstLineOfEvidence(e.PayloadJson)}");

                // One line per verdict. The founder reads this on a card; the evidence itself is whole in
                // `muthur task show`, and pasting it here turns a notification into kilobytes of escaped JSON.
                MessageService.PostFromHub(m, Recipient.Founder, null,
                    $"{Wire.TaskId(task.Id)} \"{task.Title}\" has failed validation {state.Failures} times and came back " +
                    "on the same commit, so the conductor has stopped restaffing it. Change the branch and mark it " +
                    "implemented again, re-spec it, cancel it, or raise Muthur:ConductorMaxAttempts."
                    + Environment.NewLine + string.Join(Environment.NewLine, verdicts)
                    + Environment.NewLine + $"Full evidence: muthur task show {Wire.TaskId(task.Id)}", task.Id);
                m.Record("conductor.exhausted", task.Id, new { failures = state.Failures, round = state.RoundSeq });
                _lastAction = $"stopped restaffing {Wire.TaskId(task.Id)} after {state.Failures} failures on one commit";
            }
        }, ct);
}

/// <summary>Runs conductor passes. Off unless the founder turned it on.</summary>
public sealed class ConductorWorker(IServiceProvider services, MuthurOptions options, TimeProvider clock, ILogger<ConductorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var started = await services.GetRequiredService<ConductorService>().RunPassAsync(stoppingToken);
                    if (started > 0) logger.LogInformation("Conductor started {Count} session(s).", started);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Conductor pass failed.");
                }
                await Task.Delay(TimeSpan.FromSeconds(options.EffectiveConductorIntervalSeconds), clock, stoppingToken);
            }
        }
        finally
        {
            // Here rather than anywhere later: the host waits on this method, so a session killed from inside it
            // dies before the process exits. Left to run, it would be a child no hub can account for and a task the
            // next hub staffs a second time.
            try { await services.GetRequiredService<ConductorService>().StopSessionsAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Conductor sessions could not be stopped cleanly."); }
        }
    }
}
