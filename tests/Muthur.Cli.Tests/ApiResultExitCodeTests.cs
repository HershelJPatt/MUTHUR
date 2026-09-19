using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// The one status-to-exit-code table every command shares. Agents branch on these numbers, so the whole
/// mapping is asserted in one place: a row that moves is a change to the contract, not to one command.
/// </summary>
public sealed class ApiResultExitCodeTests
{
    [Theory]
    [InlineData(200, ExitCodes.Ok)]
    [InlineData(201, ExitCodes.Ok)]
    [InlineData(202, ExitCodes.Ok)]
    [InlineData(0, ExitCodes.NotRunning)]     // nothing answered at all
    [InlineData(503, ExitCodes.NotRunning)]   // a hub that is stopping: same thing to do about it, so same code
    [InlineData(422, ExitCodes.RuleViolation)]
    [InlineData(409, ExitCodes.Conflict)]
    [InlineData(404, ExitCodes.NotFound)]
    [InlineData(401, ExitCodes.Unauthorized)]
    [InlineData(403, ExitCodes.Unauthorized)]
    [InlineData(500, ExitCodes.Error)]
    public void The_whole_table(int status, int expected) =>
        Assert.Equal(expected, new ApiResult(status, "{}").ExitCode);

    /// <summary>
    /// The exit code says "wait, or start it"; the body says which of the two happened. The CLI never parses
    /// it — it passes it through — so this is the only thing carrying `hub_stopping` to the caller.
    /// </summary>
    [Fact]
    public void A_stopping_hubs_own_words_reach_the_caller_on_stderr()
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        var body = """{"code":"hub_stopping","message":"The hub is shutting down and did not do this. Start it again with: muthur up"}""";

        var original = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            Assert.Equal(ExitCodes.NotRunning, Output.Emit(root.Parse([]), new ApiResult(503, body)));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("hub_stopping", captured.ToString(), StringComparison.Ordinal);
        Assert.Contains("muthur up", captured.ToString(), StringComparison.Ordinal);
    }
}
