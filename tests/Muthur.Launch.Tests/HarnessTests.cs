using System.Text.Json;
using Muthur.Launch;

namespace Muthur.Launch.Tests;

public sealed class HarnessTests : IDisposable
{
    [Fact]
    public void Organization_session_can_run_outside_a_repository_without_enabling_fast_mode()
    {
        var args = new CodexAdapter("codex", null).Build(Request() with { RequireRepository = false }).Arguments;
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Contains("features.fast_mode=false", args);
        Assert.DoesNotContain("--skip-git-repo-check", new CodexAdapter("codex", null).Build(Request()).Arguments);
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("high")]
    public void Claude_receives_selected_effort(string effort)
    {
        var invocation = new ClaudeAdapter().Build(Request(effort: effort));
        var arguments = invocation.Arguments.ToList();
        Assert.Equal(effort, arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void A_local_model_runs_from_a_bare_codex_home_and_the_hosted_one_keeps_the_users()
    {
        var local = Harnesses.Find("codex-oss")!.Build(Request(model: "gemma4:26b"));
        var home = Assert.Contains("CODEX_HOME", local.Environment!);
        Assert.StartsWith(_scratch, home);
        var config = File.ReadAllText(Path.Combine(home, "config.toml"));
        Assert.Contains("hooks = false", config);
        Assert.Contains("approval_policy = \"never\"", config);
        Assert.DoesNotContain("plugins", config);

        Assert.Null(Harnesses.Find("codex")!.Build(Request()).Environment);
    }

    [Fact]
    public void Only_a_local_model_session_that_must_write_git_leaves_the_sandbox()
    {
        static string SandboxOf(HarnessInvocation i) => i.Arguments[i.Arguments.ToList().IndexOf("--sandbox") + 1];
        Assert.Equal("danger-full-access", SandboxOf(Harnesses.Find("codex-oss")!.Build(Request(model: "gemma4:26b") with { RepositoryWrites = true })));
        Assert.Equal("workspace-write", SandboxOf(Harnesses.Find("codex-oss")!.Build(Request(model: "gemma4:26b"))));
        Assert.Equal("workspace-write", SandboxOf(Harnesses.Find("codex")!.Build(Request() with { RepositoryWrites = true })));
    }

    [Fact]
    public void Pi_runs_print_json_from_a_scratch_agent_directory_for_a_local_model()
    {
        var invocation = Harnesses.Find("pi")!.Build(Request(model: "gemma4:26b", effort: "low"));
        Assert.Equal("pi", invocation.FileName);
        var args = invocation.Arguments.ToList();
        foreach (var flag in new[] { "--print", "--no-session", "--no-approve", "--offline", "--no-extensions" }) Assert.Contains(flag, args);
        Assert.Equal("json", args[args.IndexOf("--mode") + 1]);
        Assert.Equal("ollama", args[args.IndexOf("--provider") + 1]);
        Assert.Equal("ollama/gemma4:26b", args[args.IndexOf("--model") + 1]);
        Assert.Equal("low", args[args.IndexOf("--thinking") + 1]);
        Assert.Equal("do the unit", invocation.Stdin);
        var home = Assert.Contains(PiAdapter.AgentDirectoryVariable, invocation.Environment!);
        Assert.Contains("\"gemma4:26b\"", File.ReadAllText(Path.Combine(home, "models.json")));

        var hosted = Harnesses.Find("pi")!.Build(Request(model: "anthropic/claude-opus-5"));
        Assert.Null(hosted.Environment);
        Assert.Equal("off", hosted.Arguments[hosted.Arguments.ToList().IndexOf("--thinking") + 1]);
    }

    [Fact]
    public void Pi_events_yield_the_final_text_and_summed_usage()
    {
        var stdout = """
            {"type":"session","version":3,"id":"x"}
            {"type":"message_end","message":{"role":"user","content":[{"type":"text","text":"hi"}]}}
            {"type":"message_end","message":{"role":"assistant","stopReason":"toolUse","content":[{"type":"toolCall","name":"bash"}],"usage":{"input":856,"output":23,"cacheRead":0,"cacheWrite":0,"totalTokens":879,"cost":{"total":0}}}}
            {"type":"message_end","message":{"role":"toolResult","content":[{"type":"text","text":"## master"}]}}
            {"type":"message_end","message":{"role":"assistant","stopReason":"stop","content":[{"type":"text","text":"STATUS: done\nBRANCH: x"}],"usage":{"input":53,"output":4,"cacheRead":1300,"cacheWrite":0,"totalTokens":1357,"cost":{"total":0.012}}}}
            {"type":"agent_end"}
            """;
        var outcome = Harnesses.Find("pi")!.Interpret(Request(), new ProcessResult(0, stdout, ""));
        Assert.True(outcome.Success);
        Assert.StartsWith("STATUS: done", outcome.Report);
        Assert.Contains("BRANCH: x", outcome.Report);
        Assert.Equal((909, 27, 1300), (outcome.InputTokens, outcome.OutputTokens, outcome.CacheReadTokens));
        Assert.Equal(0.012m, outcome.CostUsd);

        var errored = Harnesses.Find("pi")!.Interpret(Request(), new ProcessResult(0,
            """{"type":"message_end","message":{"role":"assistant","stopReason":"error","content":[{"type":"text","text":"rate limit reached"}]}}""", ""));
        Assert.False(errored.Success);
        Assert.True(errored.RateLimited);
    }

    [Fact]
    public void Claude_receives_a_turn_cap_only_when_the_catalog_sets_one()
    {
        var capped = new ClaudeAdapter().Build(Request() with { MaxTurns = 40 }).Arguments.ToList();
        Assert.Equal("40", capped[capped.IndexOf("--max-turns") + 1]);
        Assert.DoesNotContain("--max-turns", new ClaudeAdapter().Build(Request()).Arguments);
        Assert.DoesNotContain("--max-turns", new ClaudeAdapter().Build(Request() with { MaxTurns = 0 }).Arguments);
    }

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "launch-" + Guid.NewGuid().ToString("n"));

    public HarnessTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private WorkerRequest Request(string model = "opus", string? effort = null) =>
        new("C:/repo/.worktrees/w1", "do the unit", model, "C:/repo/.git", ["dotnet *", "git commit *"], ["git push*", "muthur *"], _scratch, effort);

    [Fact]
    public void The_sim_harness_is_the_cli_itself_reading_the_prompt_on_stdin()
    {
        var invocation = Harnesses.Find("sim")!.Build(Request(model: "scripted"));

        Assert.Equal("muthur", invocation.FileName);
        Assert.Equal(["sim", "agent", "--report", Path.Combine(_scratch, "sim-report.txt"), "--cd", "C:/repo/.worktrees/w1", "--model", "scripted"], invocation.Arguments);
        Assert.Equal("do the unit", invocation.Stdin);
        Assert.Null(Harnesses.Find("sim")!.WorkerNote);
        Assert.Equal("muthur", Harnesses.Find("sim")!.CapabilityExecutable);
    }

    [Theory]
    [InlineData(0, "STATUS: done | NOTES: landed", true)]
    [InlineData(0, "STATUS: blocked | NOTES: asked the founder", false)]
    [InlineData(0, null, false)]
    [InlineData(7, "STATUS: done", false)]
    public void A_sim_session_succeeds_only_when_it_exited_cleanly_and_reported_done(int exit, string? report, bool success)
    {
        var request = Request(model: "scripted");
        if (report is not null) File.WriteAllText(Path.Combine(_scratch, "sim-report.txt"), report.Replace(" | ", "\n"));

        var outcome = Harnesses.Find("sim")!.Interpret(request, new ProcessResult(exit, "", exit == 0 ? "" : "boom"));

        Assert.Equal(success, outcome.Success);
        Assert.False(outcome.RateLimited);
        if (report is not null) Assert.Contains(report.Split(" | ")[0], outcome.Report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sim's out-of-quota candidate fails the way a real CLI does: a non-zero exit with the limit on stderr. Only
    /// that combination is a limit; a clean exit that mentions one, or a crash that does not, is not.
    /// </summary>
    [Theory]
    [InlineData(1, "", "ERROR: You've hit your usage limit.", true)]
    [InlineData(1, "rate limit reached", "", true)]
    [InlineData(1, "", "boom", false)]
    [InlineData(0, "", "usage limit", false)]
    public void A_sim_session_is_rate_limited_only_on_a_failing_exit_that_says_so(int exit, string stdout, string stderr, bool limited)
    {
        var outcome = Harnesses.Find("sim")!.Interpret(Request(model: "limited"), new ProcessResult(exit, stdout, stderr));

        Assert.Equal(limited, outcome.RateLimited);
        Assert.False(outcome.Success);
        if (exit != 0) Assert.Contains($"exited with code {exit}", outcome.Report, StringComparison.Ordinal);
    }

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
        Assert.False(doc.RootElement.GetProperty("fastMode").GetBoolean());
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
    public void Codex_is_told_how_hard_to_think_when_the_candidate_says_so()
    {
        var args = Harnesses.Find("codex")!.Build(Request(model: "gpt-6-astra", effort: "high")).Arguments.ToList();

        // -c and its setting are one pair: a stray "-c" would swallow the next argument instead.
        var flag = args.IndexOf("model_reasoning_effort=\"high\"") - 1;
        Assert.True(flag >= 0);
        Assert.Equal("model_reasoning_effort=\"high\"", args[flag + 1]);
        Assert.Equal("gpt-6-astra", args[args.IndexOf("--model") + 1]);
        Assert.Equal("-", args[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Codex_says_nothing_about_reasoning_effort_when_the_candidate_does_not(string? effort)
    {
        var args = Harnesses.Find("codex")!.Build(Request(model: "gpt-6-astra", effort: effort)).Arguments.ToList();

        Assert.Contains("service_tier=\"default\"", args);
        Assert.Contains("features.fast_mode=false", args);
        Assert.DoesNotContain(args, a => a.Contains("model_reasoning_effort"));
    }

    [Fact]
    public void Claude_has_no_reasoning_effort_and_ignores_the_one_it_is_given()
    {
        var args = Harnesses.Find("claude")!.Build(Request(effort: "high")).Arguments.ToList();

        Assert.DoesNotContain("-c", args);
        Assert.DoesNotContain(args, a => a.Contains("model_reasoning_effort"));
    }

    [Fact]
    public void The_catalog_a_new_hub_gets_names_the_codex_model_and_how_hard_it_thinks()
    {
        using var doc = JsonDocument.Parse(HarnessDefaults.CatalogJson,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var codex = doc.RootElement.GetProperty("tiers").EnumerateObject()
            .SelectMany(t => t.Value.EnumerateArray())
            .Where(c => c.GetProperty("harness").GetString() == "codex")
            .ToList();

        Assert.NotEmpty(codex);
        Assert.All(codex, c =>
        {
            Assert.Equal("gpt-6-astra", c.GetProperty("model").GetString());
            Assert.Equal("medium", c.GetProperty("reasoningEffort").GetString());
        });
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

    [Fact]
    public void Claude_usage_is_read_from_the_result()
    {
        var adapter = Harnesses.Find("claude")!;
        var outcome = adapter.Interpret(Request(), new ProcessResult(0,
            """{"result":"STATUS: done","is_error":false,"total_cost_usd":0.31,"usage":{"input_tokens":1200,"output_tokens":340,"cache_read_input_tokens":9000,"cache_creation_input_tokens":10}}""", ""));
        Assert.Equal(1200, outcome.InputTokens);
        Assert.Equal(340, outcome.OutputTokens);
        Assert.Equal(9000, outcome.CacheReadTokens);
        Assert.Null(outcome.TotalTokens);

        var without = adapter.Interpret(Request(), new ProcessResult(0, """{"result":"STATUS: done","is_error":false}""", ""));
        Assert.Null(without.InputTokens);
    }

    [Theory]
    [InlineData("tokens used\n66,882\n", 66882)]
    [InlineData("tokens used: 171201", 171201)]
    [InlineData("[turn 1]\ntokens used\n1,000\n[turn 2]\ntokens used\n2,500\n", 2500)]
    [InlineData("no usage here", null)]
    public void Codex_tokens_used_is_read_from_its_output(string text, int? expected) =>
        Assert.Equal(expected, CodexAdapter.TokensUsed(text));

    [Fact]
    public void Without_json_events_codex_falls_back_to_the_total_on_its_tokens_used_line()
    {
        var adapter = Harnesses.Find("codex")!;
        var request = Request();
        var failed = adapter.Interpret(request, new ProcessResult(1, "ERROR: You've hit your usage limit.\ntokens used\n66,882", ""));
        Assert.True(failed.RateLimited);
        Assert.Equal(66882, failed.TotalTokens);

        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done\n");
        var ok = adapter.Interpret(request, new ProcessResult(0, "tokens used\n12,345\n", ""));
        Assert.True(ok.Success);
        Assert.Equal(12345, ok.TotalTokens);
        Assert.Null(ok.InputTokens);
    }

    [Fact]
    public void Codex_runs_with_json_events_on_stdout()
    {
        Assert.Contains("--json", Harnesses.Find("codex")!.Build(Request()).Arguments);
        Assert.Contains("--json", Harnesses.Find("codex-oss")!.Build(Request(model: "gemma4:26b")).Arguments);
    }

    /// <summary>
    /// Codex's input count includes what it read from the cache (16,387 of which 16,382 cached, on a real local
    /// rollout); the outcome's input is the part that was not, as Claude and pi report it, so fresh tokens compare.
    /// </summary>
    [Fact]
    public void Codex_json_events_split_fresh_input_from_cache_reads_and_sum_the_turns()
    {
        var stdout = """
            {"type":"thread.started","thread_id":"0199a213-81c0-7800-8aa1-bbab2a035a53"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"git status","exit_code":0,"status":"completed"}}
            {"type":"turn.completed","usage":{"input_tokens":16387,"cached_input_tokens":16382,"output_tokens":36}}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"STATUS: done"}}
            {"type":"turn.completed","usage":{"input_tokens":20000,"cached_input_tokens":16000,"output_tokens":400}}
            """;
        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done\n");

        var outcome = Harnesses.Find("codex")!.Interpret(Request(), new ProcessResult(0, stdout, ""));

        Assert.True(outcome.Success);
        Assert.Equal((5 + 4000, 36 + 400, 16382 + 16000), (outcome.InputTokens, outcome.OutputTokens, outcome.CacheReadTokens));
        Assert.Null(outcome.TotalTokens);
    }

    [Fact]
    public void Codex_token_count_events_are_read_as_a_running_total_when_no_turn_completed()
    {
        var stdout = """
            {"timestamp":"2026-09-22T22:02:40Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":8000,"cached_input_tokens":6000,"output_tokens":20,"reasoning_output_tokens":0,"total_tokens":8020},"last_token_usage":{"input_tokens":8000,"cached_input_tokens":6000,"output_tokens":20,"reasoning_output_tokens":0,"total_tokens":8020},"model_context_window":258400}}}
            {"timestamp":"2026-09-22T22:02:44Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":16387,"cached_input_tokens":16382,"output_tokens":36,"reasoning_output_tokens":0,"total_tokens":16423},"last_token_usage":{"input_tokens":8387,"cached_input_tokens":8382,"output_tokens":16,"reasoning_output_tokens":0,"total_tokens":8403},"model_context_window":258400}}}
            """;
        File.WriteAllText(Path.Combine(_scratch, "codex-last-message.txt"), "STATUS: done\n");

        var outcome = Harnesses.Find("codex-oss")!.Interpret(Request(), new ProcessResult(0, stdout, ""));

        Assert.Equal((5, 36, 16382), (outcome.InputTokens, outcome.OutputTokens, outcome.CacheReadTokens));
    }

    [Fact]
    public void A_failed_codex_turn_reports_its_error_and_the_usage_it_spent()
    {
        var stdout = """
            {"type":"thread.started","thread_id":"t"}
            {"type":"turn.started"}
            {"type":"error","message":"stream disconnected"}
            {"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again at 6pm."}}
            """;

        var outcome = Harnesses.Find("codex")!.Interpret(Request(), new ProcessResult(1, stdout, ""));

        Assert.False(outcome.Success);
        Assert.True(outcome.RateLimited);
        Assert.Equal("Process exited with code 1: You've hit your usage limit. Try again at 6pm.", outcome.Report);
        Assert.Null(outcome.InputTokens);
        Assert.Null(outcome.TotalTokens);
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
    public string? CodexReport { get; set; }
    public List<(string FileName, IReadOnlyCollection<string>? Scrubbed, IReadOnlyDictionary<string, string>? Environment)> Started { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin = null,
        TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        Started.Add((fileName, scrubEnvironment, environment));
        var output = arguments.ToList().IndexOf("--output-last-message");
        if (output >= 0 && CodexReport is { } report) File.WriteAllText(arguments[output + 1], report);
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
        processes.CodexReport = "STATUS: done";
        var limited = new List<string?>();

        var attempts = await new WorkerLauncher(processes, Installed, workerProcesses: new ContainedFixture(processes), admission: AdmittedFixture.Context(_scratch)).RunAsync(
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

        var attempts = await new WorkerLauncher(processes, Installed, workerProcesses: new ContainedFixture(processes), admission: AdmittedFixture.Context(_scratch)).RunAsync(
            [new("claude", "opus", "a"), new("codex", "", "b")], RequestFor, TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

        Assert.Single(attempts);
        Assert.False(attempts[0].Outcome.Success);
    }

    [Fact]
    public async Task A_missing_cli_is_skipped_and_workers_never_inherit_hub_identity()
    {
        var processes = new ScriptedProcesses(new ProcessResult(0, """{"result":"STATUS: done","is_error":false}""", ""));

        var attempts = await new WorkerLauncher(processes, name => name == "codex" ? null : Installed(name), workerProcesses: new ContainedFixture(processes), admission: AdmittedFixture.Context(_scratch)).RunAsync(
            [new("codex", "", "b"), new("claude", "opus", "a")], RequestFor, TimeSpan.FromMinutes(1), _ => Task.CompletedTask);

        Assert.Contains("not installed", attempts[0].Outcome.Report);
        Assert.True(attempts[1].Outcome.Success);
        Assert.Null(attempts[0].PromptBytes);
        Assert.Equal("prompt".Length, attempts[1].PromptBytes);
        var started = Assert.Single(processes.Started);
        Assert.Contains("MUTHUR_AGENT", started.Scrubbed!);
        Assert.Contains("MUTHUR_TOKEN", started.Scrubbed!);
    }

    [Fact]
    public async Task A_pi_worker_lands_its_unit_through_the_guard()
    {
        // What worker run hands a pi candidate: the worker deny list, the composed assignment, a worktree of its own.
        string[] denied = ["muthur *", "muthur.exe *", "git push*", "git merge*", "git rebase*", "gh *"];
        var worktree = Directory.CreateDirectory(Path.Combine(_scratch, "worktree")).FullName;
        var assignment = new WorkerAssignment(_scratch, "task/T-1", new string('a', 40), "main", "specs/T-1.md", new string('b', 40), "worker/T-1-a", worktree);
        var prompt = WorkerPrompt.Compose("# Contract", "specs/T-1.md", "A", "worker/T-1-a", ["dotnet test"], null, assignment);
        var pi = new PiWorker();

        var attempt = Assert.Single(await new WorkerLauncher(pi, Installed, workerProcesses: new ContainedFixture(pi), admission: AdmittedFixture.Context(_scratch))
            .RunAsync([new("pi", "gemma4:26b", "local")], c => new(worktree, prompt, c.Model, null, [], denied, Path.Combine(_scratch, "scratch")),
                TimeSpan.FromMinutes(1), _ => Task.CompletedTask));

        Assert.True(attempt.Outcome.Success, attempt.Outcome.Report);
        Assert.Equal("done", WorkerReport.Status(attempt.Outcome.Report));
        Assert.Equal("unit", File.ReadAllText(Path.Combine(worktree, "unit.txt")));
        Assert.Equal(["printf unit > unit.txt"], pi.Ran);
        Assert.Contains(PiGuard.BlockMarker, pi.Refused);
        Assert.Equal(1200, attempt.Outcome.InputTokens);
        var args = pi.Arguments!.ToList();
        Assert.Equal("read,bash,edit,write,grep,find,ls", args[args.IndexOf("--tools") + 1]);
        Assert.Contains(WorkerPrompt.VerifiedMarker, File.ReadAllText(args[args.IndexOf("--system-prompt") + 1]));
    }

    /// <summary>
    /// pi as the launcher sees it, playing a worker that tries the denied push and then does its unit. The guard it was
    /// handed decides the push, by <see cref="PiGuard.Denies"/> (the extension's rule in C#) over the patterns the
    /// rendered extension names; a refusal goes into the stream the way pi reports a blocked tool call.
    /// </summary>
    private sealed class PiWorker : IProcessRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public List<string> Ran { get; } = [];
        public string Refused { get; private set; } = "";

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin = null,
            TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Assert.Equal("pi", fileName);
            Arguments = arguments;
            var args = arguments.ToList();
            var guard = File.ReadAllText(args[args.IndexOf("-e") + 1]);
            var patterns = System.Text.RegularExpressions.Regex.Matches(guard, "const denied: string\\[\\] = \\[(.*)\\];").Single().Groups[1].Value
                .Split(", ").Select(p => System.Text.Json.JsonSerializer.Deserialize<string>(p)!).ToList();
            var stream = new System.Text.StringBuilder();
            foreach (var command in new[] { "git add -A && git push origin HEAD", "printf unit > unit.txt" })
            {
                if (PiGuard.Denies(patterns, command))
                {
                    Refused = $"{PiGuard.BlockMarker} \"{command}\"";
                    stream.AppendLine(System.Text.Json.JsonSerializer.Serialize(Refused));
                    continue;
                }
                Ran.Add(command);
                File.WriteAllText(Path.Combine(workingDirectory, "unit.txt"), "unit");
            }
            stream.AppendLine("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"STATUS: done"}],"stopReason":"stop","usage":{"input":1200,"output":80,"cacheRead":0}}}""");
            return Task.FromResult(new ProcessResult(0, stream.ToString(), ""));
        }
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
        processes.CodexReport = "STATUS: done";
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
        Assert.Equal("prompt".Length, attempts[1].PromptBytes);
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
        processes.CodexReport = "STATUS: done";
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
