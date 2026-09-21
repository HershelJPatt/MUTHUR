using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

public sealed class ValidationSubjectCommandTests
{
    private static RootCommand Root()
    {
        var root = new RootCommand();
        Globals.AddTo(root);
        RoleCommands.AddTo(root);
        TaskCommands.AddTo(root);
        return root;
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("yes")]
    [InlineData("fail")]
    [InlineData("no")]
    [InlineData("blocked")]
    public void Every_verdict_accepts_an_explicit_round_and_UTF8_report(string action)
    {
        var subject = Guid.NewGuid();
        var parsed = Root().Parse(["validate", action, "T-1", "--as", "win-validator", "--subject", subject.ToString(), "--evidence-file", "report.txt"]);
        Assert.Empty(parsed.Errors);
        var option = Assert.IsType<Option<Guid?>>(parsed.CommandResult.Command.Options.Single(o => o.Name == "--subject"));
        Assert.Equal(subject, parsed.GetValue(option));
    }

    [Fact]
    public async Task Contradictory_evidence_inputs_fail_before_any_hub_request()
    {
        var parsed = Root().Parse(["validate", "pass", "T-1", "--as", "win-validator", "--subject", Guid.NewGuid().ToString(),
            "--evidence", "Ran the application and checked its output.", "--evidence-file", "missing.txt"]);
        Assert.Equal(ExitCodes.RuleViolation, await parsed.InvokeAsync());
    }

    [Fact]
    public void Empty_evidence_API_errors_remain_stable_CLI_exit_two()
    {
        var error = JsonSerializer.Serialize(new ErrorResponse("evidence_required", "Supply --evidence or --evidence-file."), MuthurJsonContext.Default.ErrorResponse);
        var result = new ApiResult(422, error);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("evidence_required", JsonSerializer.Deserialize(result.Body, MuthurJsonContext.Default.ErrorResponse)!.Code);
        Assert.Equal(3, new ApiResult(409, "{}").ExitCode);
    }

    [Fact]
    public void Revalidation_requires_a_reason_and_old_requests_keep_the_subject_null()
    {
        Assert.Empty(Root().Parse(["task", "revalidate", "T-1", "--reason", "Review the new policy."]).Errors);
        Assert.NotEmpty(Root().Parse(["task", "revalidate", "T-1"]).Errors);
        var old = JsonSerializer.Deserialize("{\"validator\":\"win-validator\",\"evidence\":\"old report\"}", MuthurJsonContext.Default.VerdictRequest)!;
        Assert.Null(old.SubjectId);
        var subject = Guid.NewGuid();
        var wire = JsonSerializer.Serialize(old with { SubjectId = subject }, MuthurJsonContext.Default.VerdictRequest);
        Assert.Equal(subject, JsonSerializer.Deserialize(wire, MuthurJsonContext.Default.VerdictRequest)!.SubjectId);
    }
}
