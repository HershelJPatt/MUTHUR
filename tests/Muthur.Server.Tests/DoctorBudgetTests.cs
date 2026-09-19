using Microsoft.Extensions.Time.Testing;
using Muthur.Contracts;
using Muthur.Server;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// One doctor run is bounded whatever its checks do. T-14 gave each probe its own timeout; nothing bounded
/// the total, so several slow sources could hold one request open for minutes and the dashboard's Re-check
/// had no ceiling at all. A check that does not answer is reported, because from outside a hub that is slow
/// and a hub that is hung look the same and only one of them ends.
/// </summary>
public sealed class DoctorBudgetTests
{
    private const int Budget = 120;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private DoctorService Service(params IDoctorCheck[] checks) =>
        new(checks, _clock, new MuthurOptions { DoctorBudgetSeconds = Budget });

    /// <summary>A check that answers immediately with one row of its own.</summary>
    private sealed class Quick(string subject, CheckStatus status = CheckStatus.Ok) : IDoctorCheck
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<CheckDto>>([new CheckDto("ingest", subject, status, "ran")]);
        }
    }

    /// <summary>A check that never answers, and — the case that matters — never looks at its token either.</summary>
    private sealed class Deaf : IDoctorCheck
    {
        private readonly TaskCompletionSource<IReadOnlyList<CheckDto>> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
        {
            Calls++;
            Entered.TrySetResult();
            return _never.Task;
        }
    }

    private sealed class Broken : IDoctorCheck
    {
        public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("the probe blew up");
    }

    [Fact]
    public async Task A_check_that_never_answers_is_reported_and_the_run_still_ends()
    {
        var deaf = new Deaf();
        var run = Service(deaf).RunAsync(probe: true);
        await deaf.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));   // the run is inside the check

        // Nothing sleeps: the budget is a timer on the injected clock, so a test steps it.
        _clock.Advance(TimeSpan.FromSeconds(Budget + 1));
        var report = await run.WaitAsync(TimeSpan.FromSeconds(30));

        var unanswered = Assert.Single(report.Checks);
        Assert.Equal("doctor", unanswered.Category);
        Assert.Equal(nameof(Deaf), unanswered.Subject);
        Assert.Equal(CheckStatus.Warn, unanswered.Status);   // warn, not fail: not answering is not evidence of a broken hub
        Assert.Equal($"This check did not answer within the {Budget}s doctor budget.", unanswered.Detail);
        Assert.Equal(1, report.Warn);
        Assert.Equal(0, report.Fail);
    }

    [Fact]
    public async Task Checks_behind_the_one_that_ate_the_budget_are_reported_and_never_run()
    {
        var deaf = new Deaf();
        var after = new Quick("never-reached");
        var run = Service(deaf, after).RunAsync(probe: true);
        await deaf.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _clock.Advance(TimeSpan.FromSeconds(Budget + 1));
        var report = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, after.Calls);   // the point of the ceiling: the hub stops spending, it does not queue
        Assert.Equal(2, report.Warn);
        Assert.Contains(report.Checks, c => c.Subject == nameof(Deaf)
            && c.Detail == $"This check did not answer within the {Budget}s doctor budget.");
        Assert.Contains(report.Checks, c => c.Subject == nameof(Quick)
            && c.Detail == $"This check was not reached: the {Budget}s doctor budget was spent before it ran.");
    }

    [Fact]
    public async Task A_run_where_nothing_is_slow_is_exactly_what_it_was_before()
    {
        var first = new Quick("alpha");
        var second = new Quick("beta", CheckStatus.Warn);

        var report = await Service(first, second).RunAsync(probe: true);

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(["alpha", "beta"], report.Checks.Select(c => c.Subject));
        Assert.Equal(1, report.Ok);
        Assert.Equal(1, report.Warn);
        Assert.DoesNotContain(report.Checks, c => c.Category == "doctor");

        // And the budget is untouched, so a later run on the same clock is not starting from behind.
        _clock.Advance(TimeSpan.FromSeconds(Budget + 1));
        Assert.Equal(2, (await Service(first, second).RunAsync(probe: true)).Checks.Count);
    }

    [Fact]
    public async Task A_check_that_throws_still_fails_rather_than_being_called_slow()
    {
        var report = await Service(new Broken(), new Quick("after")).RunAsync(probe: true);

        var broken = Assert.Single(report.Checks, c => c.Category == "doctor");
        Assert.Equal(CheckStatus.Fail, broken.Status);
        Assert.Contains("the probe blew up", broken.Detail);
        Assert.Contains(report.Checks, c => c.Subject == "after");   // and the rest of the run goes on
    }

    [Fact]
    public async Task The_caller_giving_up_is_not_a_timeout()
    {
        // A cancelled request is the caller's decision, and must not come back as a report claiming the hub
        // was slow. The distinction is the whole reason the budget has its own token.
        using var caller = new CancellationTokenSource();
        var deaf = new Deaf();
        var run = Service(deaf).RunAsync(probe: true, caller.Token);
        await deaf.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
