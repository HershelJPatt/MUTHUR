using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>
/// Wakes the conductor when the ledger records something that changes what it could staff or land.
/// <para>
/// Polling alone made every phase boundary cost a whole interval: a task implemented one second after a pass
/// waited the rest of the interval for a validator, then another for integration, then another for promotion.
/// Six intervals of waiting around five seconds of work. The interval is still there as the sweep that catches
/// what no event announces (a lease lapsing, a stall probe coming due); the events are what the loop runs on.
/// </para>
/// </summary>
public sealed class ConductorTrigger : IDisposable
{
    /// <summary>Event types after which a pass could start, land or recover something it could not before.</summary>
    internal static readonly HashSet<string> Wakes = new(StringComparer.Ordinal)
    {
        "task.added", "task.implemented", "task.validated", "task.validation_failed", "task.released", "task.unblocked",
        "task.landed", "task.dependencies_ready", "task.reopened", "task.priority_set",
        "request.answered",
        "integration.passed", "integration.failed", "integration.invalidated", "integration.interrupted", "integration.promoted",
        "validation.passed", "validation.failed", "validation.subject_invalidated", "role.released",
        "conductor.child_exited", "conductor.orchestrator_exited", "conductor.on", "conductor.orchestrators_on", "conductor.sessions_set",
        "agent.limited", "agent.limit_cleared",
    };

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly IDisposable _subscription;

    public ConductorTrigger(EventFeed feed) => _subscription = feed.Subscribe(OnEvents);

    /// <summary>Whether a wake is waiting to be taken. For tests and the status line; the worker consumes it through <see cref="WaitAsync"/>.</summary>
    public bool Pending => _signal.CurrentCount > 0;

    private void OnEvents(IReadOnlyList<LedgerEvent> events)
    {
        if (!events.Any(e => Wakes.Contains(e.Type))) return;
        // At most one pending wake: a burst of events is one reason to pass, not several.
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Completes when an event wakes the conductor or the fallback elapses, whichever is first. True when an
    /// event did it, so the caller can settle a moment for the rest of the burst.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan fallback, TimeProvider clock, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(fallback, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await _signal.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _signal.Dispose();
    }
}
