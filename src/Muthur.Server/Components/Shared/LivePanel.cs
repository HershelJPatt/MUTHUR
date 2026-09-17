using Microsoft.AspNetCore.Components;
using Muthur.Core.Entities;
using Muthur.Server.Services;

namespace Muthur.Server.Components.Shared;

/// <summary>
/// Base for dashboard panels that reflect hub state. A panel loads through the application services,
/// then reloads when relevant ledger events commit — coalesced, so an event storm costs at most
/// one reload per <see cref="RefreshInterval"/>.
/// </summary>
public abstract class LivePanel : ComponentBase, IDisposable
{
    private IDisposable? _subscription;
    private Timer? _timer;
    private int _dirty;
    private int _loading;

    [Inject] protected EventFeed Feed { get; set; } = default!;

    protected virtual TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(400);

    /// <summary>Also reload on a slow cadence, for things that change with time alone (staleness, lease expiry).</summary>
    protected virtual TimeSpan? ClockInterval => null;

    protected abstract Task LoadAsync();

    /// <summary>Return false for events that cannot affect this panel.</summary>
    protected virtual bool IsRelevant(LedgerEvent e) => true;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        // A prerender pass only needs the data; feeds and timers belong to the interactive instance that follows it.
        if (!RendererInfo.IsInteractive) return;
        _subscription = Feed.Subscribe(events =>
        {
            if (events.Any(IsRelevant)) Interlocked.Exchange(ref _dirty, 1);
        });
        _timer = new Timer(_ => _ = TickAsync(), null, RefreshInterval, RefreshInterval);
        if (ClockInterval is { } clock) _clock = new Timer(_ => Interlocked.Exchange(ref _dirty, 1), null, clock, clock);
    }

    private Timer? _clock;

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        if (Interlocked.Exchange(ref _loading, 1) == 1)
        {
            Interlocked.Exchange(ref _dirty, 1); // a load is in flight; pick this change up on the next tick
            return;
        }
        try
        {
            await InvokeAsync(async () =>
            {
                await LoadAsync();
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) { }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    /// <summary>For panels that change state themselves and want an immediate refresh.</summary>
    protected async Task ReloadNowAsync()
    {
        await LoadAsync();
        StateHasChanged();
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
        _timer?.Dispose();
        _clock?.Dispose();
        GC.SuppressFinalize(this);
    }
}
