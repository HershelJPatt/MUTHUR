using System.CommandLine;
using System.Text;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// A write that fails after the manifest was judged sound, and what the repository looks like afterwards. T-8
/// left the write phase unguarded on purpose: a catch without an undo would have turned the crash into a
/// sentence and left exactly the damage behind.
/// <para>
/// The lock is the load-bearing case of the two. A pre-flight for "is this file locked" is a TOCTOU race by
/// construction — whatever it learns can be false one statement later — so an exclusive handle is the failure
/// nothing can decide in advance, and only an undo survives it. That is the remainder this task exists for,
/// and the case that must still pass after everything below.
/// </para>
/// <para>
/// The read-only case used to reach the write phase and no longer does: T-56 (landed at <c>086fee9</c>) added
/// an <c>Unwritable</c> pass to <c>ReadManifest</c> that refuses a read-only destination at validation, with
/// <c>invalid_manifest</c> and exit 2, before a byte is written. It is kept below because the repository being
/// untouched afterwards is the same claim by a different mechanism — but it proves T-56, not this task.
/// </para>
/// </summary>
[Collection(KitEnvironment.Name)]
public sealed class KitInstallRollbackTests : IDisposable
{
    private const string Harness = "scratch";

    /// <summary>CRLF, so a restore that round-tripped through `ReadAllText` would be visibly lossy.</summary>
    private static readonly byte[] OriginalB = Encoding.UTF8.GetBytes("ORIGINAL B\r\nsecond line");
    private static readonly byte[] OriginalC = Encoding.UTF8.GetBytes("ORIGINAL C");

    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly string scratch = Directory.CreateTempSubdirectory("muthur-rollback-").FullName;

    private string Kit => Path.Combine(scratch, "kit");
    private string Repository => Path.Combine(scratch, "repo");
    private string Blocked => Path.Combine(Repository, "c.md");

    public KitInstallRollbackTests()
    {
        Directory.CreateDirectory(Path.Combine(Kit, "core"));
        Directory.CreateDirectory(Path.Combine(Kit, Harness));
        File.WriteAllText(Path.Combine(Kit, "core", "dummy.md"), "dummy");
        foreach (var name in new[] { "a", "b", "c" })
            File.WriteAllText(Path.Combine(Kit, Harness, $"{name}.md"), $"{name} from the kit");

        // Three entries: one creating a directory, one overwriting a file the repository owns, and one whose
        // destination the test makes unwritable. Entry 2 is where every failure below happens.
        File.WriteAllText(Path.Combine(Kit, Harness, "kit.json"), """
            {"harness":"scratch","files":[
             {"from":"a.md","to":"docs/a.md"},
             {"from":"b.md","to":"b.md"},
             {"from":"c.md","to":"c.md"}]}
            """);

        Directory.CreateDirectory(Repository);
        File.WriteAllBytes(Path.Combine(Repository, "b.md"), OriginalB);
        File.WriteAllBytes(Blocked, OriginalC);

        Environment.SetEnvironmentVariable(KitCommands.KitVariable, Kit);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);
        if (File.Exists(Blocked)) File.SetAttributes(Blocked, FileAttributes.Normal);
        Directory.Delete(scratch, recursive: true);
    }

    /// <summary>
    /// T-56's half: a read-only destination is decidable before the write phase, so it is refused there and
    /// the write phase is never entered. The repository is untouched either way, which is what
    /// <see cref="AsItWasFound"/> asserts unchanged across both mechanisms.
    /// </summary>
    [Fact]
    public async Task A_read_only_destination_is_refused_before_the_write_phase_and_the_repository_is_untouched()
    {
        File.SetAttributes(Blocked, FileAttributes.ReadOnly);

        var (exit, code, message) = await Install();

        Assert.Equal(ExitCodes.RuleViolation, exit);
        Assert.Equal("invalid_manifest", code);
        Assert.Contains("c.md", message, StringComparison.Ordinal);
        AsItWasFound();

        File.SetAttributes(Blocked, FileAttributes.Normal);
        await InstallsCompletely();
    }

    /// <summary>
    /// The other failure mode, and the one that fails a step earlier: the read that decides `unchanged` throws
    /// before anything is journaled for c.md, so the undo is entirely of the two entries before it.
    /// </summary>
    [Fact]
    public async Task A_destination_somebody_else_holds_open_leaves_the_repository_exactly_as_it_was_found()
    {
        int exit;
        string code, message;
        using (File.Open(Blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            (exit, code, message) = await Install();

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("install_failed", code);
        Assert.Contains("while writing \"c.md\"", message, StringComparison.Ordinal);
        AsItWasFound();

        await InstallsCompletely();
    }

    /// <summary>b.md and c.md byte for byte and nothing else: no docs\, no .gitignore, no project file.</summary>
    private void AsItWasFound()
    {
        Assert.True(
            OriginalB.SequenceEqual(File.ReadAllBytes(Path.Combine(Repository, "b.md"))),
            "b.md, which entry 1 overwrote, did not come back byte for byte.");
        Assert.True(OriginalC.SequenceEqual(File.ReadAllBytes(Blocked)), "c.md was not left as it was found.");
        Assert.Equal(
            "b.md, c.md",
            string.Join(", ", Directory.GetFileSystemEntries(Repository).Select(e => Path.GetFileName(e)!).Order()));
    }

    /// <summary>Half the Goal: the failure is not terminal, and the next run installs the whole kit.</summary>
    private async Task InstallsCompletely()
    {
        Assert.Equal(ExitCodes.Ok, await Invoke("kit", "install", "--harness", Harness, "--repo", Repository));

        Assert.Equal("a from the kit", File.ReadAllText(Path.Combine(Repository, "docs", "a.md")));
        Assert.Equal("b from the kit", File.ReadAllText(Path.Combine(Repository, "b.md")));
        Assert.Equal("c from the kit", File.ReadAllText(Blocked));
        Assert.True(File.Exists(Path.Combine(Repository, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(Repository, ProjectContext.FileName)));
    }

    /// <summary>Runs the install and reads back the exit code and the error body on stderr.</summary>
    private async Task<(int Exit, string Code, string Message)> Install()
    {
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await Invoke("kit", "install", "--harness", Harness, "--repo", Repository);
        }
        finally
        {
            Console.SetError(previous);
        }

        var body = stderr.ToString();
        Assert.False(body.Length == 0, "kit install printed nothing on stderr.");

        using var error = JsonDocument.Parse(body);
        return (exit, error.RootElement.GetProperty("code").GetString()!, error.RootElement.GetProperty("message").GetString()!);
    }

    private static async Task<int> Invoke(params string[] args)
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        KitCommands.AddTo(root);

        var parse = root.Parse(args);
        Assert.Empty(parse.Errors);
        return await parse.InvokeAsync();
    }
}
