using Microsoft.Extensions.Time.Testing;
using Xunit.Sdk;

namespace Muthur.Server.Tests;

/// <summary>
/// Waiting without wall-clock guesses. Every budget here is a hang detector, not a pace: generous enough
/// that a loaded machine never trips it, so a test that does trip it has really stopped making progress.
/// </summary>
internal static class Eventually
{
    /// <summary>The real-time ceiling for anything a test waits on.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    /// <summary>Returns once the condition holds, or fails naming what never became true.</summary>
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw Expired(because);
            await Task.Delay(Poll);
        }
    }

    /// <summary>Returns once the probe answers non-null, or fails naming what never arrived.</summary>
    public static async Task<T> TrueAsync<T>(Func<Task<T?>> probe, string because) where T : class
    {
        var deadline = DateTime.UtcNow + Budget;
        while (true)
        {
            if (await probe() is { } answer) return answer;
            if (DateTime.UtcNow >= deadline) throw Expired(because);
            await Task.Delay(Poll);
        }
    }

    /// <summary>Awaits a call that something else is expected to complete, within the budget.</summary>
    public static async Task<T> CompletesAsync<T>(Task<T> call, string because)
    {
        try
        {
            return await call.WaitAsync(Budget);
        }
        catch (TimeoutException)
        {
            throw Expired(because);
        }
    }

    /// <summary>
    /// Awaits a call that ends when a server-side deadline passes, pushing the fake clock by
    /// <paramref name="step"/> until it does. The server computes that deadline when the request reaches
    /// it, which a test cannot observe, so the advance is repeated rather than done once.
    /// </summary>
    public static async Task<T> AfterAdvancingAsync<T>(Task<T> call, FakeTimeProvider clock, TimeSpan step, string because)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (!call.IsCompleted)
        {
            if (DateTime.UtcNow >= deadline) throw Expired(because);
            clock.Advance(step);
            await Task.Delay(Poll);
        }
        return await call;
    }

    private static XunitException Expired(string because) => new($"Waited {Budget.TotalSeconds:0}s and {because}");
}
