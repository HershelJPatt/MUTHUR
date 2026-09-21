namespace Muthur.XmlDocCheck.Tests;

public sealed class RepositoryFixtureCleanupTests
{
    [Fact]
    public void Missing_directory_is_success() => RepositoryFixtureCleanup.Delete(NewRoot());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_and_readonly_directories_are_removed(bool readOnly)
    {
        var root = NewRoot();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested ü")).FullName;
            var file = Path.Combine(nested, "file.txt");
            File.WriteAllText(file, "fixture");
            if (readOnly) File.SetAttributes(file, FileAttributes.ReadOnly);
            RepositoryFixtureCleanup.Delete(root);
            Assert.False(Path.Exists(root));
        }
        finally { RepositoryFixtureCleanup.Delete(root); }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void Windows_transient_failure_retries_once(int hresult)
    {
        var attempts = 0;
        var driverCalls = 0;
        RepositoryFixtureCleanup.Delete(NewRoot(), () =>
        {
            if (++attempts == 1) throw new IOException("transient", hresult);
        }, true, condition =>
        {
            driverCalls++;
            Assert.Equal(1, attempts);
            return condition();
        });
        Assert.Equal(2, attempts);
        Assert.Equal(1, driverCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Permanent_sharing_violation_exhausts_driver(int retries)
    {
        var root = NewRoot();
        var original = new IOException("held", unchecked((int)0x80070020));
        var attempts = 0;
        var error = Assert.Throws<IOException>(() => RepositoryFixtureCleanup.Delete(root, () =>
        {
            attempts++;
            throw original;
        }, true, condition =>
        {
            for (var i = 0; i < retries; i++) Assert.False(condition());
            return false;
        }));
        Assert.Equal(1 + retries, attempts);
        Assert.Same(original, error.InnerException);
        Assert.Contains(Path.GetFullPath(root), error.Message);
        Assert.Contains($"after {attempts} attempts in ", error.Message);
        Assert.Contains("0x80070020", error.Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), true)]
    [InlineData(32, true)]
    [InlineData(unchecked((int)0x80070020), false)]
    [InlineData(unchecked((int)0x80070021), false)]
    public void Other_io_failures_do_not_retry(int hresult, bool isWindows)
    {
        var original = new IOException("permanent", hresult);
        AssertImmediateFailure(original, isWindows);
    }

    [Fact]
    public void Unauthorized_failure_does_not_retry() =>
        AssertImmediateFailure(new UnauthorizedAccessException("denied"), true);

    [Fact]
    public void Delete_cannot_succeed_while_root_remains()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            Assert.Throws<IOException>(() => RepositoryFixtureCleanup.Delete(root, () => { }, true,
                _ => throw new InvalidOperationException("Unexpected retry")));
            Assert.True(Path.Exists(root));
        }
        finally { RepositoryFixtureCleanup.Delete(root); }
    }

    [Fact]
    public void Body_failure_cleans_up_and_preserves_original_stack()
    {
        var root = NewRoot();
        var original = new InvalidOperationException("body failed");
        void FailingBody()
        {
            Directory.CreateDirectory(root);
            throw original;
        }
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                RepositoryFixtureCleanup.Run(root, FailingBody));
            Assert.Same(original, error);
            Assert.Contains(nameof(FailingBody), error.StackTrace);
            Assert.False(Path.Exists(root));
        }
        finally { RepositoryFixtureCleanup.Delete(root); }
    }

    [Fact]
    public void Body_and_cleanup_failures_are_both_preserved()
    {
        var bodyError = new InvalidOperationException("body failed");
        var cleanupError = new IOException("cleanup failed");
        var error = Assert.Throws<AggregateException>(() => RepositoryFixtureCleanup.Run(NewRoot(),
            () => throw bodyError, () => throw cleanupError));
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(bodyError, first), second => Assert.Same(cleanupError, second));
    }

    [Fact]
    public void Cleanup_failure_after_successful_body_is_preserved()
    {
        var original = new IOException("cleanup failed");
        Assert.Same(original, Assert.Throws<IOException>(() =>
            RepositoryFixtureCleanup.Run(NewRoot(), () => { }, () => throw original)));
    }

    [WindowsFact]
    public void Actual_sharing_violation_succeeds_after_handle_release()
    {
        var root = NewRoot();
        FileStream? handle = null;
        try
        {
            Directory.CreateDirectory(root);
            handle = new FileStream(Path.Combine(root, "locked.txt"), FileMode.Create,
                FileAccess.ReadWrite, FileShare.None);
            var attempts = 0;
            IOException? observed = null;
            RepositoryFixtureCleanup.Delete(root, () =>
            {
                attempts++;
                try { RepositoryFixtureCleanup.DeleteAttempt(root); }
                catch (IOException error) { observed = error; throw; }
            }, true, condition =>
            {
                Assert.Equal(1, attempts);
                Assert.NotNull(observed);
                Assert.Equal(unchecked((int)0x80070020), observed.HResult);
                Assert.True(Path.Exists(root));
                handle.Dispose();
                return condition();
            });
            Assert.Equal(2, attempts);
            Assert.False(Path.Exists(root));
        }
        finally
        {
            handle?.Dispose();
            RepositoryFixtureCleanup.Delete(root);
        }
    }

    [WindowsFact]
    public void Actual_held_handle_exhausts_driver_and_remains_fatal()
    {
        var root = NewRoot();
        FileStream? handle = null;
        try
        {
            Directory.CreateDirectory(root);
            handle = new FileStream(Path.Combine(root, "locked.txt"), FileMode.Create,
                FileAccess.ReadWrite, FileShare.None);
            var attempts = 0;
            IOException? observed = null;
            var error = Assert.Throws<IOException>(() => RepositoryFixtureCleanup.Delete(root, () =>
            {
                attempts++;
                try { RepositoryFixtureCleanup.DeleteAttempt(root); }
                catch (IOException failure) { observed = failure; throw; }
            }, true, condition =>
            {
                Assert.False(condition());
                Assert.False(condition());
                return false;
            }));
            Assert.Equal(3, attempts);
            Assert.NotNull(observed);
            Assert.Equal(unchecked((int)0x80070020), observed.HResult);
            Assert.Same(observed, error.InnerException);
            Assert.Contains(root, error.Message);
            Assert.Contains("after 3 attempts in ", error.Message);
            Assert.True(Path.Exists(root));
        }
        finally
        {
            handle?.Dispose();
            RepositoryFixtureCleanup.Delete(root);
        }
    }

    private static void AssertImmediateFailure(Exception original, bool isWindows)
    {
        var attempts = 0;
        var error = Record.Exception(() => RepositoryFixtureCleanup.Delete(NewRoot(), () =>
        {
            attempts++;
            throw original;
        }, isWindows, _ => throw new InvalidOperationException("Unexpected retry")));
        Assert.Same(original, error);
        Assert.Equal(1, attempts);
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "muthur xml ü " + Guid.NewGuid().ToString("N"));

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows file sharing semantics.";
        }
    }
}
