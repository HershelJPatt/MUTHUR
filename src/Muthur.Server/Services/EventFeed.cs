using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>In-process fan-out of committed ledger events to the dashboard and to long-polling agents.</summary>
public sealed class EventFeed
{
    private readonly Lock _gate = new();
    private readonly List<Action<IReadOnlyList<LedgerEvent>>> _subscribers = [];

    public IDisposable Subscribe(Action<IReadOnlyList<LedgerEvent>> onEvents)
    {
        lock (_gate) _subscribers.Add(onEvents);
        return new Subscription(this, onEvents);
    }

    public void Publish(IReadOnlyList<LedgerEvent> events)
    {
        if (events.Count == 0) return;
        Action<IReadOnlyList<LedgerEvent>>[] targets;
        lock (_gate) targets = [.. _subscribers];
        foreach (var target in targets)
        {
            try { target(events); }
            catch (Exception) { /* a broken subscriber must never fail the mutation that already committed */ }
        }
    }

    private sealed class Subscription(EventFeed feed, Action<IReadOnlyList<LedgerEvent>> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (feed._gate) feed._subscribers.Remove(handler);
        }
    }
}
