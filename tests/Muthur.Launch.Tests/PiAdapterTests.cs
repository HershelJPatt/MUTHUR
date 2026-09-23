namespace Muthur.Launch.Tests;

public sealed class PiAdapterTests : IDisposable
{
    private static readonly string[] WorkerDenied = ["muthur *", "muthur.exe *", "git push*", "git merge*", "git rebase*", "gh *"];

    [Fact]
    public void The_invocation_loads_the_rendered_guard_by_path_under_no_extensions()
    {
        var args = new PiAdapter().Build(Request(WorkerDenied)).Arguments.ToList();

        Assert.Contains("--no-extensions", args);
        var guard = args[args.IndexOf("-e") + 1];
        Assert.Equal(Path.Combine(_scratch, PiGuard.FileName), guard);
        var extension = File.ReadAllText(guard);
        Assert.Contains("pi.on(\"tool_call\"", extension);
        foreach (var pattern in WorkerDenied) Assert.Contains($"\"{pattern}\"", extension);
        Assert.Contains(PiGuard.BlockMarker, extension);
    }

    [Fact]
    public void A_request_with_no_denials_renders_a_guard_that_registers_no_handler()
    {
        var args = new PiAdapter().Build(Request([])).Arguments.ToList();

        var extension = File.ReadAllText(args[args.IndexOf("-e") + 1]);
        Assert.Contains("export default function", extension);
        Assert.DoesNotContain("pi.on(", extension);
    }

    [Fact]
    public void A_pattern_is_written_as_a_string_literal_whatever_it_contains()
    {
        var extension = PiGuard.Extension(Request(["say \"hi\" \\ `now` ${x}*"]));

        Assert.Contains("\"say \\u0022hi\\u0022 \\\\ \\u0060now\\u0060 ${x}*\"", extension);
    }

    [Theory]
    [InlineData("git push", true)]
    [InlineData("git push --dry-run", true)]
    [InlineData("echo x && git push origin main", true)]
    [InlineData("dotnet test; gh pr create", true)]
    [InlineData("cat notes | muthur msg send", true)]
    [InlineData("false || git merge main", true)]
    [InlineData("dotnet build\n  git rebase main", true)]
    [InlineData("git status", false)]
    [InlineData("echo 'git push'", false)]
    [InlineData("muthur", false)]
    public void A_denied_glob_is_anchored_at_every_command_in_a_chain(string command, bool denied) =>
        Assert.Equal(denied, PiGuard.Denies(WorkerDenied, command));

    [Fact]
    public void A_changed_deny_list_is_a_different_capability_configuration()
    {
        var adapter = new PiAdapter();
        var workers = adapter.CapabilitySettings(Request(WorkerDenied));
        var sessions = adapter.CapabilitySettings(Request(["git push*", "gh*"]));

        Assert.NotNull(workers);
        Assert.NotEqual(workers, sessions);
        Assert.Equal(workers, adapter.CapabilitySettings(Request(WorkerDenied)));
        Assert.Equal(PiGuard.BlockMarker, adapter.GuardBlockMarker);
    }

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "pi-" + Guid.NewGuid().ToString("n"));

    public PiAdapterTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private WorkerRequest Request(IReadOnlyList<string> denied) =>
        new(Path.Combine(_scratch, "worktree"), "do the unit", "gemma4:26b", null, ["dotnet *"], denied, _scratch);
}
