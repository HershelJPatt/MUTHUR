using System.CommandLine;
using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// The sim is the loop with the model removed, so what the CLI owns is reading the launcher's prompt the way a
/// model would, choosing the scripted variation from the task body, and refusing to run anywhere a real
/// organization could be listening.
/// </summary>
public sealed class SimCommandTests
{
    [Fact]
    public void The_sim_group_offers_an_agent_and_a_run()
    {
        var root = new RootCommand("test");
        SimCommands.AddTo(root);
        var sim = root.Subcommands.Single(c => c.Name == "sim");

        Assert.Contains(sim.Subcommands, c => c.Name == "agent");
        Assert.Contains(sim.Subcommands, c => c.Name == "run");
        var parsed = root.Parse(["sim", "run", "--tasks", "3", "--pace", "0", "--auto-answer", "0", "--ask-wait", "5", "--keep"]);
        Assert.Empty(parsed.Errors);
        Assert.Empty(root.Parse(["sim", "agent", "--report", "r.txt", "--cd", ".", "--ask-wait", "5"]).Errors);
    }

    [Fact]
    public void The_ask_wait_comes_from_the_flag_then_the_run_then_a_minute()
    {
        Assert.Equal(5, SimCommands.AskWait(5));
        var was = Environment.GetEnvironmentVariable(SimCommands.AskWaitVariable);
        try
        {
            Environment.SetEnvironmentVariable(SimCommands.AskWaitVariable, "12");
            Assert.Equal(12, SimCommands.AskWait(null));
            Environment.SetEnvironmentVariable(SimCommands.AskWaitVariable, null);
            Assert.Equal(60, SimCommands.AskWait(null));
        }
        finally { Environment.SetEnvironmentVariable(SimCommands.AskWaitVariable, was); }
    }

    /// <summary>
    /// The sessions log is the run's token proxy: every scripted session writes what it was handed, in bytes, and
    /// the kit bytes its procedure sent it to, and the report divides by cold starts. A three-column line from an
    /// older agent still counts; a malformed line is skipped, never a crash at the end of a run.
    /// </summary>
    [Fact]
    public void Prompt_and_kit_bytes_are_summed_from_the_sessions_log()
    {
        var path = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n") + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "orchestrator\tT-1\t4000\t30000\nvalidator\tT-1\t1500\nimplementer\tT-1\t9000\t0\ngarbage\norchestrator\tT-2\tnotanumber\t5\n");

            Assert.Equal((3, 14500L, 30000L), SimCommands.PromptBytes(path));
            Assert.Equal((0, 0L, 0L), SimCommands.PromptBytes(path + ".missing"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// What a role reads after its prompt: the source kit's procedure, reference, template and skill for an
    /// orchestrator; procedure, brief and skill for a validator; nothing for an implementer, whose prompt inlines
    /// the contract. No kit at all is 0, not an exception at the end of a run.
    /// </summary>
    [Fact]
    public void Kit_bytes_are_the_files_the_role_is_sent_to_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
        var kit = Path.Combine(root, "kit");
        var repo = Path.Combine(root, "repo");
        try
        {
            foreach (var (relative, size) in new[] { ("core/orchestrate.md", 100), ("core/orchestrate-reference.md", 200), ("core/spec-template.md", 30),
                ("claude/skills/muthur-orchestrate.md", 40), ("core/validate.md", 50), ("briefs/validator.md", 60), ("claude/skills/muthur-validate.md", 7), ("core/implementer.md", 999) })
            {
                var file = Path.Combine(kit, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, new string('x', size));
            }
            Directory.CreateDirectory(repo);

            Assert.Equal(370, SimCommands.KitBytes("orchestrator", repo, kit));
            Assert.Equal(117, SimCommands.KitBytes("validator", repo, kit));
            Assert.Equal(0, SimCommands.KitBytes("implementer", repo, kit));
            Assert.Equal(0, SimCommands.KitBytes("orchestrator", repo, null));

            // An installed kit in the repository wins: the skill there already has the core procedure expanded into it.
            var skill = Path.Combine(repo, ".claude", "skills", "muthur-orchestrate");
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), new string('y', 1000));
            File.WriteAllText(Path.Combine(skill, "reference.md"), new string('y', 20));
            Assert.Equal(1020, SimCommands.KitBytes("orchestrator", repo, kit));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_implementer_prompt_is_recognised_by_its_assignment_block()
    {
        var prompt = """
            # Implementer contract

            You build one unit of a frozen spec.

            ---

            # Your assignment

            - Spec: `specs/T-9.md` (read all of it, and the repository's CLAUDE.md / AGENTS.md if present).
            - Your unit: **Unit A**. Only the files that unit names.
            - You are in your own git worktree, on branch `task/t-9-sim-unit-a`. Work only here; commit here; never switch branches.
            - Named local base branch: `task/t-9-sim`.
            """;

        Assert.Equal(new SessionBrief("implementer", "T-9", null), SimSession.Parse(prompt));
        Assert.Equal("specs/T-9.md", SimSession.SpecPath(prompt));
        Assert.False(SimSession.Blocked(prompt));
        Assert.True(SimSession.Blocked(prompt + "\nsim: block\n"));
    }

    [Theory]
    [InlineData("## Sim\n\ncontent: T-3 (first attempt, wrong on purpose)\n", "T-3 (first attempt, wrong on purpose)")]
    [InlineData("## Goal\n\nNothing scripted here.\n", "T-3")]
    public void The_implementer_writes_what_the_spec_says_or_the_task_id(string spec, string content) =>
        Assert.Equal(content, SimSession.ContentFor(spec, "T-3"));

    [Fact]
    public void The_scripted_spec_carries_the_block_line_only_when_asked()
    {
        Assert.True(SimSession.Blocked(SimSession.Spec("T-4", "Blocked once", "T-4", block: true)));
        Assert.False(SimSession.Blocked(SimSession.Spec("T-4", "Blocked once", "T-4", block: false)));
        Assert.Equal("T-4", SimSession.ContentFor(SimSession.Spec("T-4", "Blocked once", "T-4", block: true), "T-4"));
    }

    /// <summary>The model name is the seam for the out-of-quota candidate: only `limited` fails, and the agent accepts it as an option.</summary>
    [Theory]
    [InlineData("limited", true)]
    [InlineData("LIMITED", true)]
    [InlineData("scripted", false)]
    [InlineData(null, false)]
    public void Only_the_limited_model_fails_like_a_rate_limited_cli(string? model, bool limited)
    {
        Assert.Equal(limited, SimSession.IsLimited(model));
        Assert.Contains("usage limit", SimSession.UsageLimitMessage, StringComparison.Ordinal);

        var root = new RootCommand("test");
        SimCommands.AddTo(root);
        Assert.Empty(root.Parse(["sim", "agent", "--report", "r.txt", "--cd", ".", "--model", model ?? "scripted"]).Errors);
    }

    /// <summary>The launcher's JSON is read for its verdict wherever it landed; anything unreadable is a failed run, never a crash.</summary>
    [Fact]
    public void A_worker_run_result_is_read_off_the_launcher_json()
    {
        var done = SimSession.ReadWorkerRun(new Muthur.Launch.ProcessResult(0, """{"success":true,"status":"done","branch":"x","report":"STATUS: done"}""", ""));
        var blocked = SimSession.ReadWorkerRun(new Muthur.Launch.ProcessResult(1, "", """{"success":false,"status":"blocked","failureKind":"worker_blocked","report":"STATUS: blocked"}"""));
        var garbage = SimSession.ReadWorkerRun(new Muthur.Launch.ProcessResult(1, "", "not json {"));

        Assert.Equal(new WorkerRun(true, "done", null, "STATUS: done"), done);
        Assert.Equal(new WorkerRun(false, "blocked", "worker_blocked", "STATUS: blocked"), blocked);
        Assert.False(garbage.Success);
        Assert.Equal("worker_output_unreadable", garbage.FailureKind);
    }

    /// <summary>The implementer tier leads with the account out of quota, so every run exercises the launchers' limit handling first.</summary>
    [Fact]
    public void The_catalog_puts_the_limited_implementer_first()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(SimCommands.Catalog);
        var implementers = doc.RootElement.GetProperty("tiers").GetProperty("implementer").EnumerateArray().ToList();
        var masterminds = doc.RootElement.GetProperty("tiers").GetProperty("mastermind").EnumerateArray().ToList();

        Assert.Equal(2, implementers.Count);
        Assert.Equal("limited", implementers[0].GetProperty("model").GetString());
        Assert.Equal("sim-limited", implementers[0].GetProperty("account").GetString());
        Assert.Equal("scripted", implementers[1].GetProperty("model").GetString());
        Assert.Single(masterminds);
        Assert.Equal("scripted", masterminds[0].GetProperty("model").GetString());
    }

    /// <summary>The report's launcher counters come straight off the ledger types and payloads the hub records.</summary>
    [Fact]
    public void Worker_runs_blocks_and_limited_launches_are_counted_from_events()
    {
        var runs = new SimCommands.Runs();
        static Muthur.Contracts.EventDto Event(string type, string payload) =>
            new(1, DateTimeOffset.UnixEpoch, "x", null, type, "T-1", System.Text.Json.JsonDocument.Parse(payload).RootElement.Clone());

        runs.Count(Event("worker.finished", """{"status":"done","attempts":[{"failureKind":"quota"},{"failureKind":null}]}"""));
        runs.Count(Event("worker.failed", """{"status":"blocked"}"""));
        runs.Count(Event("conductor.session_failed", """{"error":"Process exited with code 1: ERROR: You've hit your usage limit."}"""));
        runs.Count(Event("account.limited", """{"account":"sim-limited"}"""));
        runs.Count(Event("task.claimed", "null"));

        Assert.Equal(2, runs.WorkerRuns);
        Assert.Equal(1, runs.WorkerBlocked);
        Assert.Equal(2, runs.LaunchesIntoLimitedAccount);
        Assert.Equal(1, runs.AccountLimits);
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:7466", "MUTHUR_HOME is not set")]
    [InlineData("", "http://127.0.0.1:7466", "MUTHUR_HOME is not set")]
    [InlineData("C:/scratch", "http://127.0.0.1:7420", "default port")]
    [InlineData("C:/scratch", "http://hub.example.com:7466", "not a loopback")]
    public void A_home_or_url_that_could_be_the_organization_is_refused(string? home, string url, string reason) =>
        Assert.Contains(reason, SimSession.Refusal(home, url), StringComparison.Ordinal);

    [Fact]
    public void A_scratch_home_on_a_loopback_port_is_allowed() => Assert.Null(SimSession.Refusal("C:/scratch", "http://127.0.0.1:7466"));

    [Fact]
    public void A_validator_prompt_yields_the_task_and_the_role()
    {
        var brief = SimSession.Parse("""
            You are a validator on call in this MUTHUR organization, acting as the agent in $MUTHUR_AGENT.

            Take the role `win-validator` and read its brief — it is your job description:

                muthur role take win-validator
                muthur validate claim T-12 --as win-validator
            """);

        Assert.Equal(new SessionBrief("validator", "T-12", "win-validator"), brief);
    }

    [Fact]
    public void An_orchestrator_prompt_yields_the_task_and_no_role()
    {
        var brief = SimSession.Parse("""
            You are a mastermind orchestrator in this MUTHUR organization, acting as the agent in $MUTHUR_AGENT.

            Claim T-7 ("Ship feature 7") and take it from claim to landing:

                muthur task claim T-7
            """);

        Assert.Equal(new SessionBrief("orchestrator", "T-7", null), brief);
    }

    [Theory]
    [InlineData("Do something unrelated to T-3.")]
    [InlineData("You are a validator on call. Take the role `x` but no task is named.")]
    public void A_prompt_that_is_neither_session_is_not_guessed_at(string prompt) => Assert.Null(SimSession.Parse(prompt));

    [Theory]
    [InlineData("sim: ask\n\nmore", "ask")]
    [InlineData("SIM: Bounce", "bounce")]
    [InlineData("sim: plain", "plain")]
    [InlineData("", "plain")]
    [InlineData("An ordinary task body.", "plain")]
    public void The_task_body_first_line_chooses_the_variation(string body, string mode) => Assert.Equal(mode, SimSession.Mode(body));

    [Fact]
    public void The_branch_is_named_after_the_task() => Assert.Equal("task/t-12-sim", SimSession.BranchFor("T-12"));

    [Fact]
    public void Pace_is_the_option_then_the_environment_then_the_default()
    {
        var previous = Environment.GetEnvironmentVariable(SimCommands.PaceVariable);
        try
        {
            Environment.SetEnvironmentVariable(SimCommands.PaceVariable, "250");
            Assert.Equal(40, SimCommands.Pace(40));
            Assert.Equal(250, SimCommands.Pace(null));
            Environment.SetEnvironmentVariable(SimCommands.PaceVariable, null);
            Assert.Equal(1500, SimCommands.Pace(null));
        }
        finally { Environment.SetEnvironmentVariable(SimCommands.PaceVariable, previous); }
    }
}
