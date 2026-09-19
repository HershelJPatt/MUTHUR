using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Components.Shared;
using Muthur.Server.Services;
using Xunit.Abstractions;

namespace Muthur.Server.Tests;

/// <summary>
/// The board's collision indicator, over real temp repositories. The assertion that matters most is
/// <see cref="Two_tasks_on_the_same_file_that_still_merge_are_not_a_collision"/>: sharing a file predicted
/// nothing in the measurement this feature was built on, so an indicator that fires on it would be worse
/// than none.
/// </summary>
public sealed class CollisionTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan HangDetector = TimeSpan.FromSeconds(30);
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private readonly CountingProcessRunner _processes = new(output);

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    /// <summary>The service the dashboard resolves, but over a runner that counts what it spends.</summary>
    private CollisionService Service(IProcessRunner? processes = null) => new(
        new GitLander(processes ?? _processes, _hub.PullRequests),
        processes ?? _processes,
        _hub.Services.GetRequiredService<Ledger>(),
        _hub.Clock);

    /// <summary>A task in flight on <paramref name="branch"/>: claimed, specified, implemented, awaiting a validator.</summary>
    private async Task<(HttpClient Owner, TaskDto Task)> InFlightAsync(string agent, string title, string branch)
    {
        var owner = await _hub.RegisterAgentAsync(agent);
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest(_repo.WriteSpec(task.Id)))).EnsureSuccessStatusCode();
        return (owner, await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).ReadTaskAsync());
    }

    private async Task ProjectAsync(params string[] validators)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);
        foreach (var key in validators)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key, $"Brief for {key}"))).EnsureSuccessStatusCode();
    }

    /// <summary>Puts a shared.txt of <paramref name="lines"/> numbered lines on main, for branches to edit apart.</summary>
    private void SharedFileOnMain(int lines)
    {
        _repo.Write("shared.txt", string.Concat(Enumerable.Range(1, lines).Select(i => $"line {i}\n")));
        _repo.Commit("shared file");
    }

    private void BranchEditingLine(string branch, int lines, int line, string replacement)
    {
        _repo.Git("checkout", "-q", "-b", branch, "main");
        _repo.Write("shared.txt", string.Concat(Enumerable.Range(1, lines).Select(i => i == line ? $"{replacement}\n" : $"line {i}\n")));
        _repo.Commit($"{branch}: line {line}");
        _repo.Git("checkout", "-q", "main");
    }

    [Fact]
    public async Task Two_in_flight_branches_that_do_not_merge_collide_and_both_cards_say_so()
    {
        await ProjectAsync("qa");
        _repo.BranchWithFile("task/T-1-left", "shared.txt", "left\n");
        _repo.BranchWithFile("task/T-2-right", "shared.txt", "right\n");
        var (_, left) = await InFlightAsync("first", "Rewrite the header", "task/T-1-left");
        var (_, right) = await InFlightAsync("second", "Rewrite the footer", "task/T-2-right");
        Assert.Equal(TaskState.Validating, left.State);
        Assert.Equal(TaskState.Validating, right.State);

        var collisions = await Service().CurrentAsync();
        _processes.AssertMergeTree(1);

        var collision = Assert.Single(collisions);
        Assert.Equal(left.Id, collision.TaskA);
        Assert.Equal(right.Id, collision.TaskB);
        Assert.Contains("shared.txt", collision.Files);

        // The board renders its cards inside <Virtualize>, which emits nothing until the page is interactive,
        // so the pill is asserted on the card itself. Loading the page still exercises the panel's wiring.
        Assert.Contains("panel-title", await _hub.CreateClient().GetStringAsync("/"));

        var cardA = await RenderCardAsync(left, [collision.TaskB]);
        var cardB = await RenderCardAsync(right, [collision.TaskA]);
        Assert.Contains("class=\"pill pill-collision\"", cardA);   // a class in app.css, never an inline style
        Assert.DoesNotContain("style=", cardA);
        Assert.Contains($"collides: {right.Id}", cardA);
        Assert.Contains($"collides: {left.Id}", cardB);
        Assert.DoesNotContain("pill-collision", await RenderCardAsync(left, null));
    }

    [Fact]
    public async Task Two_validated_tasks_that_do_not_merge_collide_that_being_the_moment_it_matters_most()
    {
        // No required validators, so 'implemented' validates outright — the shape the end-to-end run hit,
        // where both tasks were queued to land and the board said nothing. Validated is the highest-stakes
        // of the three states: the bounce is not a future risk, it is one land away.
        await ProjectAsync();
        _repo.BranchWithFile("task/T-1-left", "shared.txt", "left\n");
        _repo.BranchWithFile("task/T-2-right", "shared.txt", "right\n");
        var (_, left) = await InFlightAsync("first", "Rewrite the header", "task/T-1-left");
        var (_, right) = await InFlightAsync("second", "Rewrite the footer", "task/T-2-right");
        Assert.Equal(TaskState.Validated, left.State);
        Assert.Equal(TaskState.Validated, right.State);

        var collisions = await Service().CurrentAsync();
        _processes.AssertMergeTree(1);
        var collision = Assert.Single(collisions);

        Assert.Equal(left.Id, collision.TaskA);
        Assert.Equal(right.Id, collision.TaskB);
        Assert.Contains("shared.txt", collision.Files);
    }

    [Fact]
    public async Task Two_tasks_on_the_same_file_that_still_merge_are_not_a_collision()
    {
        await ProjectAsync("qa");
        const int lines = 60;
        SharedFileOnMain(lines);
        BranchEditingLine("task/T-1-top", lines, 2, "the top, rewritten");
        BranchEditingLine("task/T-2-bottom", lines, 58, "the bottom, rewritten");
        await InFlightAsync("first", "Rework the top", "task/T-1-top");
        await InFlightAsync("second", "Rework the bottom", "task/T-2-bottom");

        // The whole finding this feature rests on: these two really are on the same file.
        Assert.Contains("shared.txt", _repo.Git("diff", "--name-only", "main...task/T-1-top"));
        Assert.Contains("shared.txt", _repo.Git("diff", "--name-only", "main...task/T-2-bottom"));

        var collisions = await Service().CurrentAsync();
        _processes.AssertMergeTree(0);
        Assert.Empty(collisions);
        Assert.DoesNotContain("pill-collision", await _hub.CreateClient().GetStringAsync("/"));
    }

    [Fact]
    public async Task A_second_call_inside_the_cache_window_runs_no_further_git_commands()
    {
        var (left, right) = await ConflictingTasksAsync();
        var runner = new ScriptedProcessRunner(_repo.Path);
        var service = Service(runner);

        var first = await service.CurrentAsync().WaitAsync(HangDetector);
        AssertCollision(first, left, right);
        runner.AssertPasses(1);

        var second = await service.CurrentAsync().WaitAsync(HangDetector);
        Assert.Same(first, second);
        runner.AssertPasses(1);

        // And it is the window that holds it, not a one-shot: past 60 seconds it asks again.
        _hub.Clock.Advance(TimeSpan.FromSeconds(61));
        AssertCollision(await service.CurrentAsync().WaitAsync(HangDetector), left, right);
        runner.AssertPasses(2);
    }

    [Fact]
    public async Task A_caller_that_arrives_mid_pass_takes_the_cached_answer_instead_of_queueing_behind_it()
    {
        var (left, right) = await ConflictingTasksAsync();
        var runner = new ScriptedProcessRunner(_repo.Path);
        var gate = runner.ArmGate();
        var service = Service(runner);

        var pass = service.CurrentAsync();
        try
        {
            await gate.Entered.WaitAsync(HangDetector);
            Assert.False(pass.IsCompleted);
            runner.AssertPasses(1);

            // The second panel answers before release, with the empty initial cache.
            Assert.Empty(await service.CurrentAsync().WaitAsync(HangDetector));
            Assert.False(pass.IsCompleted);
            runner.AssertPasses(1);

            gate.Release();
            var completed = await pass.WaitAsync(HangDetector);
            AssertCollision(completed, left, right);
            Assert.Same(completed, await service.CurrentAsync().WaitAsync(HangDetector));
            runner.AssertPasses(1);
        }
        finally
        {
            gate.Release();
            await pass.WaitAsync(HangDetector);
        }
    }

    [Fact]
    public async Task A_caller_during_refresh_takes_the_old_collision_until_the_clean_pass_completes()
    {
        var (left, right) = await ConflictingTasksAsync();
        var runner = new ScriptedProcessRunner(_repo.Path);
        var service = Service(runner);
        var old = await service.CurrentAsync().WaitAsync(HangDetector);
        AssertCollision(old, left, right);
        runner.AssertPasses(1);

        _hub.Clock.Advance(TimeSpan.FromSeconds(61));
        runner.MergeResult = new ProcessResult(0, ScriptedProcessRunner.TreeOid + "\n", "");
        var gate = runner.ArmGate();
        var refresh = service.CurrentAsync();
        try
        {
            await gate.Entered.WaitAsync(HangDetector);
            Assert.False(refresh.IsCompleted);
            runner.AssertPasses(2);

            Assert.Same(old, await service.CurrentAsync().WaitAsync(HangDetector));
            Assert.False(refresh.IsCompleted);
            runner.AssertPasses(2);

            gate.Release();
            var completed = await refresh.WaitAsync(HangDetector);
            Assert.Empty(completed);
            Assert.NotSame(old, completed);
            Assert.Same(completed, await service.CurrentAsync().WaitAsync(HangDetector));
            runner.AssertPasses(2);
        }
        finally
        {
            gate.Release();
            await refresh.WaitAsync(HangDetector);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(124)]
    [InlineData(127)]
    public async Task Only_exit_one_is_evidence_of_a_collision_even_with_conflict_shaped_output(int exitCode)
    {
        await ConflictingTasksAsync();
        var runner = new ScriptedProcessRunner(_repo.Path);
        runner.MergeResult = runner.MergeResult with { ExitCode = exitCode };

        Assert.Empty(await Service(runner).CurrentAsync().WaitAsync(HangDetector));
        runner.AssertPasses(1);
    }

    private async Task<(TaskDto Left, TaskDto Right)> ConflictingTasksAsync()
    {
        await ProjectAsync("qa");
        _repo.BranchWithFile("task/T-1-left", "shared.txt", "left\n");
        _repo.BranchWithFile("task/T-2-right", "shared.txt", "right\n");
        var (_, left) = await InFlightAsync("first", "Rewrite the header", "task/T-1-left");
        var (_, right) = await InFlightAsync("second", "Rewrite the footer", "task/T-2-right");
        return (left, right);
    }

    private static void AssertCollision(IReadOnlyList<Collision> collisions, TaskDto left, TaskDto right)
    {
        var collision = Assert.Single(collisions);
        Assert.Equal(left.Id, collision.TaskA);
        Assert.Equal(right.Id, collision.TaskB);
        Assert.Equal("shared.txt", Assert.Single(collision.Files));
    }

    [Fact]
    public async Task Tasks_that_are_not_in_flight_or_have_no_branch_are_never_compared()
    {
        // Both branches conflict, so anything wrongly left in the filter shows up as a collision.
        await ProjectAsync();   // no required validators: 'implemented' validates, so the task can land
        _repo.BranchWithFile("task/T-1-left", "shared.txt", "left\n");
        _repo.BranchWithFile("task/T-2-right", "shared.txt", "right\n");

        var (owner, landed) = await InFlightAsync("first", "Rewrite the header", "task/T-1-left");
        Assert.Equal(TaskState.Validated, landed.State);
        var done = await (await owner.PostAsync(Routes.TaskAction(landed.Id, "land"), null)).ReadTaskAsync();
        Assert.Equal(TaskState.Done, done.State);
        Assert.Equal("task/T-1-left", done.Branch);   // a done task keeps its branch, and must still be ignored

        var (second, validated) = await InFlightAsync("second", "Rewrite the footer", "task/T-2-right");
        Assert.Equal(TaskState.Validated, validated.State);
        var cancelled = await (await second.PostActionAsync(validated.Id, "cancel", new CancelTaskRequest("superseded"))).ReadTaskAsync();
        Assert.Equal(TaskState.Cancelled, cancelled.State);
        Assert.Equal("task/T-2-right", cancelled.Branch);   // validated is compared now, cancelled still is not

        var claimed = await _hub.RegisterAgentAsync("third");
        var noBranch = await claimed.AddTaskAsync("Still thinking");
        (await claimed.ClaimAsync(noBranch.Id)).EnsureSuccessStatusCode();

        var backlog = await _hub.RegisterAgentAsync("fourth");
        await backlog.AddTaskAsync("Not started");

        Assert.Empty(await Service().CurrentAsync());
        Assert.Equal(0, _processes.Calls);   // fewer than two candidates: it never reaches git at all
    }

    /// <summary>One card as the board builds it. The board's own cards live inside Virtualize, which a prerender leaves empty.</summary>
    private async Task<string> RenderCardAsync(TaskDto task, IReadOnlyList<string>? collidesWith)
    {
        using var scope = _hub.Services.CreateScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TaskCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TaskCard.Task)] = task,
                [nameof(TaskCard.Now)] = _hub.Clock.GetUtcNow(),
                [nameof(TaskCard.CollidesWith)] = collidesWith,
            }));
            return output.ToHtmlString();
        });
    }

    /// <summary>Real Git integration, with process evidence retained in the test result.</summary>
    private sealed class CountingProcessRunner(ITestOutputHelper output) : IProcessRunner
    {
        private readonly ProcessRunner _real = new();
        private readonly ConcurrentQueue<MergeResult> _merges = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void AssertMergeTree(int expectedExitCode)
        {
            var merge = Assert.Single(_merges);
            Assert.Equal(TimeSpan.FromSeconds(5), merge.RequestedTimeout);
            Assert.Equal(HangDetector, merge.EffectiveTimeout);
            Assert.Equal(expectedExitCode, merge.Result.ExitCode);
        }

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Interlocked.Increment(ref _calls);
            var isMergeTree = fileName == "git" && arguments.Count > 0 && arguments[0] == "merge-tree";
            // Integration proves Git's merge semantics; the scripted runner checks the responsiveness contract.
            var effectiveTimeout = isMergeTree ? HangDetector : timeout;
            var elapsed = Stopwatch.StartNew();
            try
            {
                var result = await _real.RunAsync(fileName, arguments, workingDirectory, stdin, effectiveTimeout, ct, scrubEnvironment, environment);
                if (isMergeTree) _merges.Enqueue(new MergeResult(timeout, effectiveTimeout, result));
                output.WriteLine($"Process: {fileName}; arguments: [{string.Join(", ", arguments)}]; working directory: {workingDirectory}; " +
                    $"elapsed milliseconds: {elapsed.Elapsed.TotalMilliseconds}; requested timeout: {timeout}; effective test timeout: {effectiveTimeout}; " +
                    $"exit code: {result.ExitCode}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
                return result;
            }
            catch (Exception exception)
            {
                output.WriteLine($"Process: {fileName}; arguments: [{string.Join(", ", arguments)}]; working directory: {workingDirectory}; " +
                    $"elapsed milliseconds: {elapsed.Elapsed.TotalMilliseconds}; requested timeout: {timeout}; effective test timeout: {effectiveTimeout}; " +
                    $"exception: {exception}");
                throw;
            }
        }

        private sealed record MergeResult(TimeSpan? RequestedTimeout, TimeSpan? EffectiveTimeout, ProcessResult Result);
    }

    /// <summary>Exact service-pass calls only: no external process can influence the cache assertions.</summary>
    private sealed class ScriptedProcessRunner(string repoPath) : IProcessRunner
    {
        public const string TreeOid = "1111111111111111111111111111111111111111";
        private static readonly string[][] PassArguments =
        [
            ["rev-parse", "--verify", "--quiet", "refs/heads/task/T-1-left"],
            ["rev-parse", "--verify", "--quiet", "refs/heads/task/T-2-right"],
            ["merge-tree", "--write-tree", "--name-only", "task/T-1-left", "task/T-2-right"],
        ];
        private readonly ConcurrentQueue<Call> _calls = new();
        private readonly ConcurrentQueue<string> _unexpected = new();
        private MergeGate? _gate;

        public ProcessResult MergeResult { get; set; } = new(1,
            TreeOid + "\nshared.txt\n\nCONFLICT (content): Merge conflict in shared.txt\n", "");

        public MergeGate ArmGate() => _gate = new MergeGate();

        public void AssertPasses(int count)
        {
            // CollisionService catches failures, so unexpected calls must be asserted outside it.
            Assert.Empty(_unexpected);
            var calls = _calls.ToArray();
            Assert.Equal(count * 3, calls.Length);
            for (var i = 0; i < calls.Length; i++)
            {
                Assert.Equal("git", calls[i].FileName);
                Assert.Equal(repoPath, calls[i].WorkingDirectory);
                Assert.Equal(PassArguments[i % 3], calls[i].Arguments);
                if (i % 3 == 2) Assert.Equal(TimeSpan.FromSeconds(5), calls[i].Timeout);
            }
        }

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            _calls.Enqueue(new Call(fileName, arguments.ToArray(), workingDirectory, timeout));
            var command = Array.FindIndex(PassArguments, expected => expected.SequenceEqual(arguments));
            if (fileName != "git" || workingDirectory != repoPath || command < 0 || stdin is not null ||
                scrubEnvironment is not null || environment is not null)
            {
                var unexpected = $"{fileName} {string.Join(' ', arguments)} in {workingDirectory}";
                _unexpected.Enqueue(unexpected);
                throw new InvalidOperationException($"Unexpected scripted process call: {unexpected}");
            }

            ct.ThrowIfCancellationRequested();
            if (command < 2) return new ProcessResult(0, TreeOid + "\n", "");
            if (_gate is { } gate) await gate.WaitAsync(ct);
            return MergeResult;
        }

        private sealed record Call(string FileName, string[] Arguments, string WorkingDirectory, TimeSpan? Timeout);
    }

    private sealed class MergeGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _released.TrySetResult();

        public async Task WaitAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(HangDetector, ct);
        }
    }
}
