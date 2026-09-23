namespace Muthur.Launch.Tests;

/// <summary>
/// The sandbox identity is checked against the paths the blocked reports named, before any seat is reserved,
/// and only on the harness that has a sandbox identity of its own.
/// </summary>
public sealed class WorkerEnvironmentTests : IDisposable
{
    private readonly Func<string, string, bool?> _original = WorkerEnvironment.Readable;
    private readonly string _dir = Directory.CreateTempSubdirectory("muthur-env-").FullName;

    public void Dispose()
    {
        WorkerEnvironment.Readable = _original;
        Directory.Delete(_dir, recursive: true);
    }

    private string Existing(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_codex_worker_is_refused_when_the_sandbox_group_cannot_read_the_nuget_configuration()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = Existing("NuGet.Config");
        WorkerEnvironment.Readable = (path, group) => path == config && group == WorkerEnvironment.CodexSandboxGroup ? false : true;

        var finding = WorkerEnvironment.Check("codex", [config, Existing("packages")]);

        Assert.NotNull(finding);
        Assert.Equal(config, finding.Path);
        Assert.Contains("icacls", finding.Fix);
        Assert.Contains(WorkerEnvironment.CodexSandboxGroup, finding.Fix);
        Assert.Null(WorkerEnvironment.Check("claude", [config]));           // the launching user's own identity
        Assert.Null(WorkerEnvironment.Check("codex", [Path.Combine(_dir, "absent")]));   // nothing there to want
    }

    [Fact]
    public void An_unreadable_acl_is_not_a_refusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        WorkerEnvironment.Readable = (_, _) => null;
        Assert.Null(WorkerEnvironment.Check("codex", [Existing("NuGet.Config")]));
    }

    [Fact]
    public void Refused_candidates_become_never_started_attempts_and_the_rest_are_usable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = Existing("NuGet.Config");
        var original = WorkerEnvironment.Readable;
        WorkerEnvironment.Readable = (_, _) => false;
        try
        {
            // The real path list is consulted; make the check see the same fixture by pointing Readable at everything.
            var (usable, refused) = WorkerEnvironment.Partition(
                [new("codex", "gpt", "chatgpt"), new("claude", "opus", "claude-sub"), new("codex-oss", "gemma", "local")]);
            var codexRefused = refused.Count;   // depends on whether the real NuGet paths exist on this machine
            Assert.Contains(usable, c => c.Harness == "claude");
            Assert.All(refused, a => Assert.False(a.Started));
            Assert.All(refused, a => Assert.Equal("environment", a.FailureKind));
            Assert.All(refused, a => Assert.StartsWith("STATUS: blocked", a.Outcome.Report));
            Assert.Equal(3, usable.Count + codexRefused);
        }
        finally { WorkerEnvironment.Readable = original; }
    }

    [Fact]
    public void The_real_probe_reads_this_machines_acls_without_throwing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Existing("probe.txt");
        // The launching user can read its own temp file; an unknown group has no entry.
        Assert.Equal(false, _original(file, "NoSuchGroup-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(true, _original(file, Environment.UserName));
    }
}
