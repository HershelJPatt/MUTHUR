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

    [Theory]
    [InlineData(SessionRoles.Implementer, "read,bash,edit,write,grep,find,ls")]
    [InlineData(SessionRoles.Orchestrator, "read,bash,edit,write,grep,find,ls")]
    [InlineData(SessionRoles.Validator, "read,bash,grep,find,ls")]
    public void Each_seat_gets_its_own_tools_and_a_muthur_system_prompt_in_place_of_pis(string role, string tools)
    {
        var args = new PiAdapter().Build(Request(WorkerDenied) with { Role = role }).Arguments.ToList();

        Assert.Equal(tools, args[args.IndexOf("--tools") + 1]);
        Assert.Equal(Path.Combine(_scratch, PiAdapter.SystemPromptFileName), args[args.IndexOf("--system-prompt") + 1]);
        Assert.Contains($"You are a MUTHUR {role}", File.ReadAllText(args[args.IndexOf("--system-prompt") + 1]));
        // Project rules (AGENTS.md, CLAUDE.md) must still load: only --no-context-files turns them off.
        Assert.DoesNotContain("-nc", args);
        Assert.DoesNotContain("--no-context-files", args);
    }

    [Fact]
    public void The_system_prompt_names_the_worktree_the_launchers_checks_and_the_deny_list()
    {
        var worktree = Path.Combine(_scratch, "worktree");
        var assignment = new WorkerAssignment("C:/repo", "task/T-7", "0123456789abcdef0123456789abcdef01234567", "main",
            "specs/T-7.md", "89abcdef0123456789abcdef0123456789abcdef", "work/T-7-a", worktree);
        var prompt = WorkerPrompt.Compose("# Contract", "specs/T-7.md", "A", "work/T-7-a", ["dotnet test"], null, assignment);

        var system = PiAdapter.SystemPrompt(Request(WorkerDenied) with { Prompt = prompt });

        Assert.Contains($"`{worktree.Replace('\\', '/')}`", system);
        Assert.Contains(WorkerPrompt.VerifiedMarker, system);
        Assert.Contains("`0123456789abcdef0123456789abcdef01234567`", system);
        Assert.Contains("Never change global safe.directory", system);
        foreach (var pattern in WorkerDenied) Assert.Contains($"`{pattern}`", system);
        Assert.Contains("POSIX shell", system);
        Assert.DoesNotContain("# Contract", system);
    }

    [Fact]
    public void A_session_without_an_assignment_or_denials_gets_neither_block()
    {
        var system = PiAdapter.SystemPrompt(Request([]) with { Role = SessionRoles.Validator });

        Assert.DoesNotContain(WorkerPrompt.VerifiedMarker, system);
        Assert.DoesNotContain("You may not run", system);
        Assert.Contains("you do not change the work you judge", system);
    }

    [Fact]
    public void The_scratch_catalog_window_follows_the_candidate()
    {
        static int Window(string home) =>
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "models.json"))).RootElement
                .GetProperty("providers").GetProperty("ollama").GetProperty("models")[0].GetProperty("contextWindow").GetInt32();
        var adapter = new PiAdapter();

        Assert.Equal(PiAdapter.DefaultContextWindow, Window(adapter.Build(Request([])).Environment![PiAdapter.AgentDirectoryVariable]));
        Assert.Equal(65536, Window(adapter.Build(Request([]) with { ContextWindow = 65536 }).Environment![PiAdapter.AgentDirectoryVariable]));
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
