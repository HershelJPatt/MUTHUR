namespace Muthur.Server.Tests;

/// <summary>
/// The rule that decides whether a test run keeps a hub's data directory. Every line below is a real
/// FileLogger line, because the whole point is that the level is read out of the format the hub actually
/// writes.
/// </summary>
public sealed class TestHubDirectoriesTests
{
    private const string Information =
        "2026-01-01T12:00:00.0000000+00:00 Information Muthur.Server.Infrastructure.Startup: Hub listening on http://127.0.0.1:7777";

    private const string Error =
        "2026-01-01T12:00:01.0000000+00:00 Error       Muthur.Server.Api.Errors: Unhandled error on PUT /outbound/targets";

    private const string Critical =
        "2026-01-01T12:00:02.0000000+00:00 Critical    Microsoft.AspNetCore.Hosting.Diagnostics: Application startup exception";

    [Fact]
    public void A_log_of_ordinary_lines_is_not_evidence()
    {
        Assert.False(TestHubDirectories.LoggedAnError([]));
        Assert.False(TestHubDirectories.LoggedAnError([""]));
        Assert.False(TestHubDirectories.LoggedAnError(["   "]));
        Assert.False(TestHubDirectories.LoggedAnError([Information, Information]));
    }

    [Fact]
    public void One_error_or_critical_line_anywhere_is_evidence()
    {
        Assert.True(TestHubDirectories.LoggedAnError([Error]));
        Assert.True(TestHubDirectories.LoggedAnError([Critical]));
        Assert.True(TestHubDirectories.LoggedAnError([Information, "", Error, Information]));
    }

    [Fact]
    public void The_level_is_read_from_the_level_column_and_not_from_the_message()
    {
        // The message of a perfectly ordinary line can say anything, including the word this rule looks for.
        Assert.False(TestHubDirectories.LoggedAnError(
            ["2026-01-01T12:00:00.0000000+00:00 Information Muthur.Server.Services.Doctor: Error budget unchanged; no Critical findings"]));

        // A stack trace continues an Error line without repeating its shape, and is not itself a match.
        Assert.False(TestHubDirectories.LoggedAnError(["   at Muthur.Server.Api.Errors.Handle()"]));

        // Fewer than two tokens is not a match either.
        Assert.False(TestHubDirectories.LoggedAnError(["Error"]));
    }

    [Fact]
    public void Releasing_a_directory_that_is_already_gone_is_not_a_failure()
    {
        // A restart test brings a second hub up over the same DataDir, so the second disposal finds nothing.
        TestHubDirectories.Release(Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n")));
    }

    [Fact]
    public void A_delete_that_fails_and_then_succeeds_is_reported_once_as_removed()
    {
        // Every hub is released twice, and the handle that defeats the first attempt is gone by the second.
        var dir = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var held = new FileStream(Path.Combine(dir, "held.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            TestHubDirectories.Release(dir);

            Assert.True(Directory.Exists(dir), "a directory holding an open file cannot be deleted");
            var reported = Assert.Single(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
            Assert.StartsWith("  failed  ", reported, StringComparison.Ordinal);
        }
        finally
        {
            held.Dispose();
        }

        TestHubDirectories.Release(dir);

        Assert.False(Directory.Exists(dir));
        // Removed directories are counted, not listed, so the earlier failure must no longer be named.
        Assert.DoesNotContain(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
    }
}
