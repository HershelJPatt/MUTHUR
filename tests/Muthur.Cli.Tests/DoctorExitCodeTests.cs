using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// The whole point of `muthur doctor` is its exit code: a conductor runs it before it staffs anything.
/// Only a failed check turns it red — a warning is worth reading, not worth stopping for.
/// </summary>
public sealed class DoctorExitCodeTests
{
    private static ApiResult Report(params CheckDto[] checks)
    {
        var dto = new DoctorDto(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Probed: true,
            Ok: checks.Count(c => c.Status is CheckStatus.Ok),
            Warn: checks.Count(c => c.Status is CheckStatus.Warn),
            Fail: checks.Count(c => c.Status is CheckStatus.Fail),
            checks);
        return new ApiResult(200, JsonSerializer.Serialize(dto, MuthurJsonContext.Default.DoctorDto));
    }

    private static CheckDto Check(CheckStatus status) => new("repo", "scratch", status, "because.");

    [Fact]
    public void A_clean_report_is_zero()
    {
        var result = Report(Check(CheckStatus.Ok), Check(CheckStatus.Ok));
        Assert.Equal(ExitCodes.Ok, SystemCommands.DoctorExitCode(result, result.ExitCode));
    }

    [Fact]
    public void An_empty_report_is_zero()
    {
        var result = Report();
        Assert.Equal(ExitCodes.Ok, SystemCommands.DoctorExitCode(result, result.ExitCode));
    }

    [Fact]
    public void A_warning_never_changes_the_exit_code()
    {
        var result = Report(Check(CheckStatus.Ok), Check(CheckStatus.Warn), Check(CheckStatus.Warn));
        Assert.Equal(ExitCodes.Ok, SystemCommands.DoctorExitCode(result, result.ExitCode));
    }

    [Fact]
    public void One_failed_check_is_one()
    {
        var result = Report(Check(CheckStatus.Ok), Check(CheckStatus.Warn), Check(CheckStatus.Fail));
        Assert.Equal(ExitCodes.Error, SystemCommands.DoctorExitCode(result, result.ExitCode));
    }

    [Fact]
    public void A_hub_that_is_not_running_keeps_its_own_exit_code()
    {
        var result = new ApiResult(0, """{"code":"not_running","message":"MUTHUR is not reachable."}""");
        Assert.Equal(ExitCodes.NotRunning, SystemCommands.DoctorExitCode(result, result.ExitCode));
    }

    [Fact]
    public void An_error_response_keeps_its_own_exit_code()
    {
        var result = new ApiResult(422, """{"code":"nope","message":"no."}""");
        Assert.Equal(ExitCodes.RuleViolation, SystemCommands.DoctorExitCode(result, result.ExitCode));
    }

    [Fact]
    public void Whatever_printing_already_objected_to_wins()
    {
        // Emit owns the exit code whenever it returns one of its own; the report is only consulted past that.
        var result = Report(Check(CheckStatus.Ok));
        Assert.Equal(ExitCodes.Unauthorized, SystemCommands.DoctorExitCode(result, ExitCodes.Unauthorized));
    }
}
