using System.Text.Json;
using Muthur.Launch;

namespace Muthur.Launch.Tests;

public sealed class HarnessTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "launch-" + Guid.NewGuid().ToString("n"));

    public HarnessTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private WorkerRequest Request(string model = "opus") =>
        new("C:/repo/.worktrees/w1", "do the unit", model, "C:/repo/.git", ["dotnet *", "git commit *"], ["git push*", "muthur *"], _scratch);

    [Fact]
    public void Claude_runs_headless_with_permissions_in_a_settings_file_and_the_prompt_on_stdin()
    {
        var invocation = Harnesses.Find("claude")!.Build(Request());

        Assert.Equal("claude", invocation.FileName);
        Assert.Equal("do the unit", invocation.Stdin);
        Assert.Contains("--print", invocation.Arguments);
        Assert.Equal("opus", invocation.Arguments[invocation.Arguments.ToList().IndexOf("--model") + 1]);
        Assert.DoesNotContain(invocation.Arguments, a => a.Contains('(')); // nothing that needs shell quoting

        var settings = invocation.Arguments[invocation.Arguments.ToList().IndexOf("--settings") + 1];
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        var permissions = doc.RootElement.GetProperty("permissions");
        Assert.Equal(["Bash(dotnet *)", "Bash(git commit *)"], permissions.GetProperty("allow").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["Bash(git push*)", "Bash(muthur *)"], permissions.GetProperty("deny").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Claude_json_result_is_interpreted()
    {
        var adapter = Harnesses.Find("claude")!;
        var ok = adapter.Interpret(Request(), new ProcessResult(0, """{"result":"STATUS: done","is_error":false,"total_cost_usd":0.42,"session_id":"abc"}""", ""));
        Assert.True(ok.Success);
        Assert.Equal("STATUS: done", ok.Report);
        Assert.Equal(0.42m, ok.CostUsd);

        var limited = adapter.Interpret(Request(), new ProcessResult(1, """{"result":"Claude usage limit reached. Resets at 5pm.","is_error":true}""", ""));
        Assert.False(limited.Success);
        Assert.True(limited.RateLimited);

        var garbage = adapter.Interpret(Request(), new ProcessResult(1, "not json", "boom"));
        Assert.False(garbage.Success);
        Assert.False(garbage.RateLimited);
        Assert.Equal("boom", garbage.Report);
    }

    [Fact]
    public void Codex_runs_sandboxed_in_the_worktree_and_may_write_the_shared_git_directory()
    {
        var args = Harnesses.Find("codex")!.Build(Request(model: "")).Arguments.ToList();

        Assert.Equal("exec", args[0]);
        Assert.Equal("C:/repo/.worktrees/w1", args[args.IndexOf("--cd") + 1]);
        Assert.Equal("workspace-write", args[args.IndexOf("--sandbox") + 1]);
        Assert.Equal("C:/repo/.git", args[args.IndexOf("--add-dir") + 1]);
        Assert.DoesNotContain("--model", args); // empty model = the harness default
        Assert.DoesNotContain("--oss", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void Local_models_go_through_the_same_harness_with_the_open_source_provider()
    {
        var args = Harnesses.Find("codex-oss")!.Build(Request(model: "gpt-oss:20b")).Arguments.ToList();

        Assert.Contains("--oss", args);
        Assert.Equal("ollama", args[args.IndexOf("--local-provider") + 1]);
        Assert.Equal("gpt-oss:20b", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public void Codex_report_is_the_last_message_file()
    {
        var adapter = Harnesses.Find("codex")!;
        var request = Request();
        Assert.False(adapter.Interpret(request, new ProcessResult(0, "", "")).Success); // exit 0 but no report is not success

        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done\nBRANCH: x\n");
        var outcome = adapter.Interpret(request, new ProcessResult(0, "", ""));
        Assert.True(outcome.Success);
        Assert.StartsWith("STATUS: done", outcome.Report);
    }

    [Theory]
    [InlineData("Error: 429 Too Many Requests", true)]
    [InlineData("You've hit your usage limit. Try again at 6pm.", true)]
    [InlineData("rate_limit_error", true)]
    [InlineData("error CS1002: ; expected", false)]
    public void Rate_limits_are_recognized(string text, bool expected) => Assert.Equal(expected, Harnesses.LooksRateLimited(text));

    [Fact]
    public void The_prompt_carries_the_contract_and_the_assignment()
    {
        var prompt = WorkerPrompt.Compose("# Implementer\nrules…", "specs/T-7.md", "Unit B — parser", "worker/t-7-unit-b-ab12cd", ["dotnet build", "dotnet test"], "watch the null case");

        Assert.StartsWith("# Implementer", prompt);
        Assert.Contains("`specs/T-7.md`", prompt);
        Assert.Contains("**Unit B — parser**", prompt);
        Assert.Contains("`worker/t-7-unit-b-ab12cd`", prompt);
        Assert.Contains("    dotnet test", prompt);
        Assert.Contains("watch the null case", prompt);
    }
}

/// <summary>
/// Hands back one scripted result per launch and records what each launch was given. Both launchers are driven
/// through it, because what separates them is the identity and scrubbing they pass — which is only visible here.
/// </summary>
internal sealed class ScriptedProcesses(params ProcessResult[] results) : IProcessRunner
{
    private readonly Queue<ProcessResult> _results = new(results);
    public List<(string FileName, IReadOnlyCollection<string>? Scrubbed, IReadOnlyDictionary<string, string>? Environment)> Started { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        Started.Add((fileName, scrubEnvironment, environment));
        return Task.FromResult(_results.Dequeue());
    }
}

public sealed class WorkerLauncherTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "launch-" + Guid.NewGuid().ToString("n"));

    public WorkerLauncherTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private WorkerRequest RequestFor(HarnessCandidate c) => new(_scratch, "prompt", c.Model, null, [], [], _scratch);

    private static (string, IReadOnlyList<string>)? Installed(string name) => (name, []);

    [Fact]
    public async Task An_exhausted_account_falls_through_to_the_next_candidate_and_is_reported()
    {
        var processes = new ScriptedProcesses(
            new ProcessResult(1, """{"result":"usage limit reached","is_error":true}""", ""),
            new ProcessResult(0, "", ""));
        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done");
        var limited = new List<string?>();

        var attempts = await new WorkerLauncher(processes, Installed).RunAsync(
            [new("claude", "opus", "claude-sub"), new("codex", "", "chatgpt-sub")], RequestFor, TimeSpan.FromMinutes(1),
            c => { limited.Add(c.Account); return Task.CompletedTask; });

        Assert.Equal(2, attempts.Count);
        Assert.True(attempts[0].Outcome.RateLimited);
        Assert.True(attempts[1].Outcome.Success);
        Assert.Equal("codex", attempts[1].Candidate.Harness);
        Assert.Equal(["claude-sub"], limited);
        // Two attempts is also what a crash-and-retry looks like; the order of the processes started is not.
        Assert.Equal(["claude", "codex"], processes.Started.Select(s => s.FileName));
    }

    [Fact]
    public async Task A_worker_that_ran_and_failed_is_not_retried_elsewhere()
    {
        var processes = new ScriptedProcesses(new ProcessResult(1, """{"result":"tests fail","is_error":true}""", ""));

        var attempts = await new WorkerLauncher(processes, Installed).RunAsync(
            [new("claude", "opus", "a"), new("codex", "", "b")], RequestFor, TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

        Assert.Single(attempts);
        Assert.False(attempts[0].Outcome.Success);
    }

    [Fact]
    public async Task A_missing_cli_is_skipped_and_workers_never_inherit_hub_identity()
    {
        var processes = new ScriptedProcesses(new ProcessResult(0, """{"result":"STATUS: done","is_error":false}""", ""));

        var attempts = await new WorkerLauncher(processes, name => name == "codex" ? null : Installed(name)).RunAsync(
            [new("codex", "", "b"), new("claude", "opus", "a")], RequestFor, TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

        Assert.Contains("not installed", attempts[0].Outcome.Report);
        Assert.True(attempts[1].Outcome.Success);
        var started = Assert.Single(processes.Started);
        Assert.Contains("MUTHUR_AGENT", started.Scrubbed!);
        Assert.Contains("MUTHUR_TOKEN", started.Scrubbed!);
    }
}

public sealed class WorkerReportTests
{
    [Theory]
    [InlineData("STATUS: done\nBRANCH: x", "done")]
    [InlineData("Here you go.\n\n**STATUS:** blocked\n", "blocked")]
    [InlineData("status: spec-problem", "spec-problem")]
    [InlineData("all good, no report", null)]
    public void The_status_line_decides(string report, string? expected) => Assert.Equal(expected, WorkerReport.Status(report));
}

/// <summary>
/// The line between a worker and an agent session is authority, and it is asserted in both directions here so
/// the two launchers can never quietly converge.
/// </summary>
public sealed class AgentLauncherTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "launch-" + Guid.NewGuid().ToString("n"));

    public AgentLauncherTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private WorkerRequest RequestFor(HarnessCandidate c) => new(_scratch, "prompt", c.Model, null, [], [], _scratch);

    private static (string, IReadOnlyList<string>)? Installed(string name) => (name, []);

    private static Task<AgentIdentity> IdentityFor(HarnessCandidate c) =>
        Task.FromResult(new AgentIdentity($"validator-{c.Harness}", $"tok-{c.Harness}"));

    [Fact]
    public void An_agent_session_is_given_the_identity_a_worker_is_denied()
    {
        var environment = AgentLauncher.EnvironmentFor(new AgentIdentity("conductor-win-validator", "tok_abc"));

        Assert.Equal("conductor-win-validator", environment["MUTHUR_AGENT"]);
        Assert.Equal("tok_abc", environment["MUTHUR_TOKEN"]);
    }

    [Fact]
    public void The_identity_is_exactly_the_two_variables_the_cli_reads()
    {
        var environment = AgentLauncher.EnvironmentFor(new AgentIdentity("a", "b"));

        Assert.Equal(["MUTHUR_AGENT", "MUTHUR_TOKEN"], environment.Keys.Order());
    }

    [Fact]
    public async Task An_exhausted_account_falls_through_to_the_next_candidate()
    {
        var processes = new ScriptedProcesses(
            new ProcessResult(1, """{"result":"usage limit reached","is_error":true}""", ""),
            new ProcessResult(0, "", ""));
        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done");
        var limited = new List<string?>();

        var attempts = await new AgentLauncher(processes, Installed).RunAsync(
            [new("claude", "opus", "claude-sub"), new("codex", "", "chatgpt-sub")], RequestFor, IdentityFor,
            TimeSpan.FromMinutes(1), c => { limited.Add(c.Account); return Task.CompletedTask; });

        Assert.Equal("claude", attempts[0].Candidate.Harness);
        Assert.True(attempts[0].Outcome.RateLimited);
        Assert.Equal(["claude-sub"], limited);
        Assert.Equal("codex", attempts[1].Candidate.Harness);
        Assert.True(attempts[1].Outcome.Success);
        Assert.Equal(2, attempts.Count);
    }

    [Fact]
    public async Task A_session_that_ran_and_failed_is_not_retried_elsewhere()
    {
        var processes = new ScriptedProcesses(new ProcessResult(1, """{"result":"the verdict is fail","is_error":true}""", ""));
        var limited = new List<string?>();

        var attempts = await new AgentLauncher(processes, Installed).RunAsync(
            [new("claude", "opus", "a"), new("codex", "", "b")], RequestFor, IdentityFor,
            TimeSpan.FromMinutes(1), c => { limited.Add(c.Account); return Task.CompletedTask; });

        // A session that ran and gave a verdict is finished, right or wrong: only an account that could not
        // answer at all is worth trying elsewhere, so nothing here may reach the second vendor.
        Assert.Single(attempts);
        Assert.False(attempts[0].Outcome.Success);
        Assert.Empty(limited);
    }

    [Fact]
    public async Task The_identity_is_resolved_for_the_candidate_that_actually_runs()
    {
        var processes = new ScriptedProcesses(
            new ProcessResult(1, """{"result":"usage limit reached","is_error":true}""", ""),
            new ProcessResult(0, "", ""));
        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done");
        var asked = new List<string>();

        await new AgentLauncher(processes, Installed).RunAsync(
            [new("claude", "opus", "claude-sub"), new("codex", "", "chatgpt-sub")], RequestFor,
            c => { asked.Add(c.Harness); return IdentityFor(c); },
            TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

        Assert.Equal(["claude", "codex"], asked);
        Assert.Equal("validator-claude", processes.Started[0].Environment!["MUTHUR_AGENT"]);
        Assert.Equal("validator-codex", processes.Started[1].Environment!["MUTHUR_AGENT"]);
    }

    [Fact]
    public async Task A_missing_cli_is_skipped()
    {
        var processes = new ScriptedProcesses(new ProcessResult(0, """{"result":"STATUS: done","is_error":false}""", ""));
        var asked = new List<string>();

        var attempts = await new AgentLauncher(processes, name => name == "codex" ? null : Installed(name)).RunAsync(
            [new("codex", "", "b"), new("claude", "opus", "a")], RequestFor,
            c => { asked.Add(c.Harness); return IdentityFor(c); },
            TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

        Assert.Contains("not installed", attempts[0].Outcome.Report);
        Assert.False(attempts[0].Started);
        Assert.True(attempts[1].Outcome.Success);
        Assert.Single(processes.Started);
        // A candidate that never reaches a process resolves no identity: identityFor is called for the
        // candidate about to run, so a skipped one must not mint a token for a session that never happens.
        Assert.Equal(["claude"], asked);
    }
}
