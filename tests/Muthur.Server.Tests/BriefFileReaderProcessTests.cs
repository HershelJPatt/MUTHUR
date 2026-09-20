using Muthur.Core;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class BriefFileReaderProcessTests : IDisposable
{
    private const string Relative = "briefs/role [draft].md";
    private const string Contents = "# Role\r\n\r\nExact brief text.\n";
    private readonly string _root = Directory.CreateTempSubdirectory("muthur-brief-process-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteBrief()
    {
        Directory.CreateDirectory(Path.Combine(_root, "briefs"));
        File.WriteAllText(Path.Combine(_root, Relative), Contents);
    }

    [Fact]
    public async Task A_modified_brief_is_refused_without_a_tracked_file_probe()
    {
        WriteBrief();
        using var cancellation = new CancellationTokenSource();
        var processes = new FakeProcessRunner(new(0, "true\n", ""), new(0, " M role [draft].md\n", ""));

        var refused = await Assert.ThrowsAsync<MuthurException>(() =>
            new BriefFileReader(processes).ReadAsync([_root], Relative, cancellation.Token));

        AssertDirty(refused);
        AssertProbes(processes, cancellation.Token, tracked: false);
    }

    [Fact]
    public async Task An_untracked_brief_is_refused_after_an_empty_status()
    {
        WriteBrief();
        using var cancellation = new CancellationTokenSource();
        var processes = new FakeProcessRunner(new(0, "true\n", ""), new(0, "", ""),
            new(1, "", "pathspec did not match any files"));

        var refused = await Assert.ThrowsAsync<MuthurException>(() =>
            new BriefFileReader(processes).ReadAsync([_root], Relative, cancellation.Token));

        AssertDirty(refused);
        AssertProbes(processes, cancellation.Token, tracked: true);
    }

    [Fact]
    public async Task A_clean_tracked_brief_returns_the_exact_file_text()
    {
        WriteBrief();
        using var cancellation = new CancellationTokenSource();
        var processes = new FakeProcessRunner(new(0, "true\n", ""), new(0, "", ""),
            new(0, "role [draft].md\n", ""));

        var text = await new BriefFileReader(processes).ReadAsync([_root], Relative, cancellation.Token);

        Assert.Equal(Contents, text);
        AssertProbes(processes, cancellation.Token, tracked: true);
    }

    [Theory]
    [InlineData("", true, "brief_file_required")]
    [InlineData("   ", true, "brief_file_required")]
    [InlineData(Relative, false, "no_repository")]
    [InlineData(Relative, true, "brief_file_missing")]
    public async Task Invalid_input_is_refused_before_any_process_call(string path, bool hasRoot, string code)
    {
        var processes = new FakeProcessRunner();

        var refused = await Assert.ThrowsAsync<MuthurException>(() =>
            new BriefFileReader(processes).ReadAsync(hasRoot ? [_root] : [], path));

        Assert.Equal(code, refused.Code);
        Assert.Equal(ErrorKind.RuleViolation, refused.Kind);
        Assert.Empty(processes.Calls);
        Assert.Empty(processes.Results);
    }

    private void AssertDirty(MuthurException refused)
    {
        Assert.Equal("brief_file_dirty", refused.Code);
        Assert.Equal(ErrorKind.RuleViolation, refused.Kind);
        Assert.Contains($"'{Relative}'", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, refused.Message, StringComparison.Ordinal);
    }

    private void AssertProbes(FakeProcessRunner processes, CancellationToken token, bool tracked)
    {
        // Check recorded calls outside RunAsync: RepoFile's best-effort fallback catches process exceptions.
        Assert.Equal(tracked ? 3 : 2, processes.Calls.Count);
        Assert.Empty(processes.Results);
        Assert.All(processes.Calls, call =>
        {
            Assert.Equal("git", call.Executable);
            Assert.Equal(Path.Combine(_root, "briefs"), call.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);
            Assert.Equal(token, call.Token);
            Assert.True(call.Token.CanBeCanceled);
            Assert.False(call.Token.IsCancellationRequested);
        });
        Assert.Equal(["rev-parse", "--is-inside-work-tree"], processes.Calls[0].Arguments);
        Assert.Equal(["--literal-pathspecs", "status", "--porcelain", "--untracked-files=no", "--", "role [draft].md"],
            processes.Calls[1].Arguments);
        if (tracked)
            Assert.Equal(["--literal-pathspecs", "ls-files", "--error-unmatch", "--", "role [draft].md"],
                processes.Calls[2].Arguments);
    }

    private sealed record ProcessCall(string Executable, string[] Arguments, string WorkingDirectory,
        CancellationToken Token, TimeSpan? Timeout);

    private sealed class FakeProcessRunner(params ProcessResult[] results) : IProcessRunner
    {
        public Queue<ProcessResult> Results { get; } = new(results);
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
            IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(new(fileName, [.. arguments], workingDirectory, ct, timeout));
            Assert.NotEmpty(Results);
            return Task.FromResult(Results.Dequeue());
        }
    }
}
