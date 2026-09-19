using System.Diagnostics;
using Muthur.Contracts;

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
    public void A_directory_whose_log_recorded_an_error_is_kept_and_named()
    {
        var dir = NewDataDir(Information, Error);

        TestHubDirectories.Release(dir);

        Assert.True(Directory.Exists(dir), "a log that recorded an error is the only copy of that evidence");
        var reported = Assert.Single(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
        Assert.StartsWith("  kept    ", reported, StringComparison.Ordinal);
        Assert.EndsWith($"(1 error line in {MuthurEnvironment.LogFile})", reported, StringComparison.Ordinal);

        // Leave nothing behind: the removal itself is asserted by the test below.
        TestHubDirectories.Release(dir, expectsLoggedErrors: true);
    }

    [Fact]
    public void A_hub_that_expects_its_errors_has_its_directory_removed_anyway()
    {
        // Retention means unexpected. A test that logs an error on purpose has asserted on it already, so its
        // log is evidence of nothing and keeping it rebuilds the leak at a slower rate.
        var dir = NewDataDir(Information, Error);

        TestHubDirectories.Release(dir, expectsLoggedErrors: true);

        Assert.False(Directory.Exists(dir));
        Assert.DoesNotContain(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_that_cannot_be_read_keeps_the_directory()
    {
        // Evidence is cheaper than a lost test run: whatever stopped the read, the answer is to keep the
        // directory and name it, never to delete on the strength of a log nobody managed to look at.
        var dir = NewDataDir(Information);
        var log = new FileStream(Path.Combine(dir, MuthurEnvironment.LogFile), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            TestHubDirectories.Release(dir);

            Assert.True(Directory.Exists(dir));
            var reported = Assert.Single(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
            Assert.StartsWith("  kept    ", reported, StringComparison.Ordinal);
            Assert.Contains($"({MuthurEnvironment.LogFile} could not be read:", reported, StringComparison.Ordinal);
        }
        finally
        {
            log.Dispose();
        }

        // Once the handle is gone the log reads as the ordinary log it always was, and the directory goes.
        TestHubDirectories.Release(dir);

        Assert.False(Directory.Exists(dir));
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

    [Fact]
    public void A_directory_held_open_for_the_whole_retry_budget_is_failed_and_reddens_the_run()
    {
        // ExitCode() answers for the whole run, so these tests pin the causal claim — this outcome reddens
        // the run, that one leaves it alone — rather than a literal zero. Other classes are releasing
        // directories in parallel, and the hand proof of the exit hook leaks one on purpose.
        var before = TestHubDirectories.ExitCode();
        var dir = NewHeldDir(out var held);
        try
        {
            TestHubDirectories.Release(dir);

            var reported = Assert.Single(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
            Assert.StartsWith("  failed  ", reported, StringComparison.Ordinal);
            Assert.NotEqual(0, TestHubDirectories.ExitCode());
        }
        finally
        {
            held.Dispose();
        }

        // Put the run back where it was: this suite must not redden itself proving that a real leak would.
        TestHubDirectories.Release(dir);

        Assert.False(Directory.Exists(dir));
        Assert.Equal(before, TestHubDirectories.ExitCode());
    }

    [Fact]
    public void A_handle_released_within_the_retry_budget_ends_removed()
    {
        // The case the retry exists for: a scanner lets go a moment after the first delete lost to it.
        var before = TestHubDirectories.ExitCode();
        var dir = NewHeldDir(out var held);
        var swept = Path.Combine(dir, "swept.marker");
        File.WriteAllText(swept, "");
        using (held)
        {
            // A recursive delete that throws still removes every file it could, so the marker's disappearance
            // is an observable "one attempt has been made and lost", and the handle goes on that condition
            // rather than on a clock. The watcher gets a thread of its own because the thread pool is busy
            // running the rest of the suite, and a release queued behind that work would miss the budget it
            // is meant to land inside.
            var watcher = new Thread(() =>
            {
                var deadline = DateTime.UtcNow + Eventually.Budget;
                while (File.Exists(swept) && DateTime.UtcNow < deadline) Thread.Sleep(1);
                held.Dispose();
            }) { IsBackground = true };
            watcher.Start();

            var spent = Stopwatch.StartNew();
            TestHubDirectories.Release(dir);
            spent.Stop();

            Assert.True(watcher.Join(Eventually.Budget), "the watcher never let go of the handle");
            Assert.False(Directory.Exists(dir));
            Assert.DoesNotContain(TestHubDirectories.Summary(), line => line.Contains(dir, StringComparison.Ordinal));
            Assert.Equal(before, TestHubDirectories.ExitCode());

            // Without a backoff paid, the handle was gone before the first attempt reached it and this test
            // would be the happy path in disguise. The retry removed the directory only if it waited first.
            Assert.True(spent.Elapsed >= TimeSpan.FromMilliseconds(40), $"removed in {spent.ElapsedMilliseconds}ms, so no attempt was ever retried");
        }
    }

    [Fact]
    public void Removing_and_keeping_directories_leaves_the_exit_code_alone()
    {
        // A kept directory is evidence the retention rule preserved on purpose, not a leak, so neither it nor
        // a removed one reddens the run. Only Failed does, which the test above pins.
        var before = TestHubDirectories.ExitCode();
        var kept = NewDataDir(Information, Error);
        var removed = NewDataDir(Information);

        TestHubDirectories.Release(kept);
        TestHubDirectories.Release(removed);

        Assert.True(Directory.Exists(kept));
        Assert.False(Directory.Exists(removed));
        Assert.Equal(before, TestHubDirectories.ExitCode());

        TestHubDirectories.Release(kept, expectsLoggedErrors: true);
    }

    /// <summary>A data directory no delete can empty, because <paramref name="held"/> excludes every sharer.</summary>
    private static string NewHeldDir(out FileStream held)
    {
        var dir = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        held = new FileStream(Path.Combine(dir, "held.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return dir;
    }

    /// <summary>A data directory holding the log lines given, as a disposed hub would have left it.</summary>
    private static string NewDataDir(params string[] logLines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, MuthurEnvironment.LogFile), logLines);
        return dir;
    }
}
