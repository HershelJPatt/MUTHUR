using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>
/// What gets the conductor out of its chair between passes: a task crossing one of the handoffs it staffs.
/// Subscribed to the ledger's event feed for the life of the hub; the worker waits on it with the pass
/// interval as the fallback.
/// </summary>
public sealed class ConductorWake : IDisposable
{
    /// <summary>
    /// The event types that are a handoff the conductor acts on. Nothing else wakes it. The last two are the
    /// bounce-backs: an owner that exited on a question or a failed verdict is released and restaffed by the
    /// pass, so the answer and the verdict are handoffs too.
    /// </summary>
    public static readonly IReadOnlySet<string> Transitions = new HashSet<string>(StringComparer.Ordinal)
    {
        "task.claimed", "task.implemented", "task.validated", "integration.passed",
        "request.answered", "task.validation_failed",
    };

    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;
    private readonly TimeProvider _clock;
    private string? _pending;
    private TaskCompletionSource<string?>? _waiter;

    public ConductorWake(EventFeed feed, TimeProvider clock)
    {
        _clock = clock;
        _subscription = feed.Subscribe(OnEvents);
    }

    private void OnEvents(IReadOnlyList<LedgerEvent> events)
    {
        try
        {
            foreach (var e in events)
            {
                if (!Transitions.Contains(e.Type)) continue;
                Nudge(e.Type);
                return;
            }
        }
        catch (Exception) { /* a wake-up must never fail the mutation that already committed */ }
    }

    /// <summary>
    /// Records that a pass is wanted. Idempotent while one is pending: any number of transitions before the
    /// next wait collapse into one wake-up, and a transition that lands during a pass is kept for the wait
    /// that follows it, never lost.
    /// </summary>
    public void Nudge(string reason)
    {
        TaskCompletionSource<string?>? waiter;
        lock (_gate)
        {
            waiter = _waiter;
            if (waiter is null) { _pending ??= reason; return; }
            _waiter = null;
        }
        waiter.TrySetResult(reason);
    }

    /// <summary>
    /// Completes with the reason of the pending nudge as soon as there is one (immediately, if a nudge arrived
    /// before the call), or with null once <paramref name="fallback"/> has elapsed on the clock. Consumes the
    /// pending nudge. One waiter at a time: the worker is the only caller.
    /// </summary>
    public async Task<string?> WaitAsync(TimeSpan fallback, CancellationToken ct)
    {
        TaskCompletionSource<string?> waiter;
        lock (_gate)
        {
            if (_pending is { } pending) { _pending = null; return pending; }
            waiter = _waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            var delay = Task.Delay(fallback, _clock, ct);
            if (await Task.WhenAny(waiter.Task, delay) == delay) await delay;   // surfaces the cancellation
            return waiter.Task.IsCompletedSuccessfully ? waiter.Task.Result : null;
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_waiter, waiter)) _waiter = null;
        }
    }

    /// <summary>Whether a nudge is waiting to be consumed. For tests and status only.</summary>
    public bool Pending { get { lock (_gate) return _pending is not null; } }

    public void Dispose() => _subscription.Dispose();
}
