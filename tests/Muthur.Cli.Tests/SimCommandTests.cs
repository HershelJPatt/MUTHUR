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
        var parsed = root.Parse(["sim", "run", "--tasks", "3", "--pace", "0", "--auto-answer", "0", "--keep"]);
        Assert.Empty(parsed.Errors);
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
