using Muthur.Contracts;

namespace Muthur.Launch.Tests;

public sealed class ProbeReservationRunnerTests
{
    private static readonly ProbeAdmissionRequest Request = new("T-1", "worker", "fixture", "model", "account", Guid.NewGuid().ToString("N"));

    private sealed class Client : IProbeAdmissionClient
    {
        public bool MayExecute { get; init; } = true;
        public Exception? AdmissionFailure { get; init; }
        public Exception? ReleaseFailure { get; init; }
        public int Releases { get; private set; }
        public Action<CancellationToken>? OnRelease { get; init; }
        public Task<ProbeAdmissionDto> AdmitAsync(ProbeAdmissionRequest request, CancellationToken ct = default) =>
            AdmissionFailure is { } failure ? Task.FromException<ProbeAdmissionDto>(failure) : Task.FromResult(new ProbeAdmissionDto("reservation", request.Task, request.RunId, MayExecute));
        public Task ReleaseAsync(ProbeReleaseRequest request, CancellationToken ct = default)
        {
            Assert.Equal("reservation", request.ReservationId);
            Releases++;
            OnRelease?.Invoke(ct);
            return ReleaseFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Replay_never_executes_or_releases()
    {
        var client = new Client { MayExecute = false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeReservationRunner(client)
            .RunAsync<int>(Request, _ => throw new Exception("Must not run")));
        Assert.Contains("reservation", error.Message);
        Assert.Contains("replay", error.Message);
        Assert.Equal(0, client.Releases);
    }

    [Fact]
    public async Task Refusal_never_executes_or_releases()
    {
        var failure = new InvalidOperationException("capacity");
        var client = new Client { AdmissionFailure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeReservationRunner(client)
            .RunAsync<int>(Request, _ => throw new Exception("Must not run"))));
        Assert.Equal(0, client.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_waits_for_callback_cleanup_even_when_cancelled(bool cancel)
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var client = new Client { OnRelease = token => { Assert.True(cleaned); Assert.False(token.IsCancellationRequested); Assert.True(token.CanBeCanceled); } };
        var execution = new ProbeReservationRunner(client).RunAsync(Request, async token =>
        {
            entered.SetResult();
            try { await finishCleanup.Task; token.ThrowIfCancellationRequested(); return 42; }
            finally { cleaned = true; }
        }, caller.Token);
        await entered.Task;
        if (cancel) caller.Cancel();
        Assert.Equal(0, client.Releases);
        finishCleanup.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        else Assert.Equal(42, await execution);
        Assert.Equal(1, client.Releases);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failures_preserve_callback_and_identify_failed_release(bool callbackFails, bool releaseFails)
    {
        var callbackFailure = new ApplicationException("callback");
        var releaseFailure = new ApplicationException("release");
        var client = new Client { ReleaseFailure = releaseFails ? releaseFailure : null };
        var execution = new ProbeReservationRunner(client).RunAsync(Request, _ => callbackFails
            ? Task.FromException<int>(callbackFailure) : Task.FromResult(42));
        if (callbackFails && releaseFails)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => execution);
            Assert.Same(callbackFailure, error.InnerExceptions[0]);
            Assert.Contains("reservation", error.InnerExceptions[1].Message);
            Assert.Same(releaseFailure, error.InnerExceptions[1].InnerException);
        }
        else if (callbackFails) Assert.Same(callbackFailure, await Assert.ThrowsAsync<ApplicationException>(() => execution));
        else if (releaseFails) Assert.Contains("reservation", (await Assert.ThrowsAsync<InvalidOperationException>(() => execution)).Message);
        else Assert.Equal(42, await execution);
        Assert.Equal(1, client.Releases);
    }
}
