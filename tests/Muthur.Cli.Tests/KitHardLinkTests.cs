using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

[Collection(KitEnvironment.Name)]
public sealed class KitHardLinkTests : IDisposable
{
    private readonly string scratch = Directory.CreateTempSubdirectory("muthur-hardlinks-").FullName;
    private readonly string? previousKit = Environment.GetEnvironmentVariable(KitCommands.KitVariable);
    private readonly List<string> directoryLinks = [];
    private string Repo => Path.Combine(scratch, "repo");
    private string Kit => Path.Combine(scratch, "kit");
    private string Harness => Path.Combine(Kit, "probe");
    private string Manifest => Path.Combine(Harness, "kit.json");
    private string Source => Path.Combine(Harness, "source.md");
    private string Alias => Path.Combine(scratch, "outside-別名.md");

    public KitHardLinkTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Harness);
        File.WriteAllText(Source, "new\n");
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, Kit);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitCommands.KitVariable, previousKit);
        foreach (var link in directoryLinks) Directory.Delete(link);
        Directory.Delete(scratch, true);
    }

    private void WriteManifest(string to = "é-文件.md", string? mode = "replace", bool late = false, bool duplicate = false)
    {
        var entries = new List<Dictionary<string, string>>();
        if (late) entries.Add(new() { ["from"] = "source.md", ["to"] = "new/nested/safe.md" });
        var entry = new Dictionary<string, string> { ["from"] = "source.md", ["to"] = to };
        if (mode is not null) entry["mode"] = mode;
        entries.Add(entry);
        if (duplicate)
        {
            File.WriteAllText(Path.Combine(Harness, "restore.md"), "old\n");
            entries.Add(new() { ["from"] = "restore.md", ["to"] = to });
        }
        File.WriteAllText(Manifest, JsonSerializer.Serialize(new { files = entries }));
    }

    private static void RequireNative()
    {
        var platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Create("unknown");
        Assert.Null(HardLinkInspector.PlatformFailure(platform, RuntimeInformation.ProcessArchitecture, BitConverter.IsLittleEndian));
    }

    private static void Link(string alias, string target)
    {
        RequireNative(); // Unsupported hosts fail explicitly; they never report fake native coverage.
        var success = OperatingSystem.IsWindows() ? CreateHardLink(alias, target, IntPtr.Zero) : UnixLink(target, alias) == 0;
        Assert.True(success, $"Native hard-link fixture failed: OS error {Marshal.GetLastPInvokeError()}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string target, IntPtr security);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string alias);

    private async Task<(int Exit, string Error)> Install()
    {
        var root = new RootCommand("test");
        Globals.AddTo(root);
        KitCommands.AddTo(root);
        using var error = new StringWriter();
        using var output = new StringWriter();
        var parse = root.Parse(["kit", "install", "--harness", "probe", "--repo", Repo]);
        var previousError = Console.Error;
        var previousOutput = Console.Out;
        try
        {
            Console.SetError(error);
            Console.SetOut(output);
            var exit = await parse.InvokeAsync();
            return (exit, error.ToString());
        }
        finally
        {
            Console.SetError(previousError);
            Console.SetOut(previousOutput);
        }
    }

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(scratch, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(scratch, p), p => Convert.ToBase64String(File.ReadAllBytes(p)));

    private void Unchanged(Dictionary<string, string> before)
    {
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
        Assert.False(Directory.Exists(Path.Combine(Repo, "new")));
    }

    [Fact]
    public void Native_counts_and_unicode_paths()
    {
        RequireNative();
        var path = Path.Combine(Repo, "é-文件.md");
        File.WriteAllText(path, "old\n");
        Assert.Equal(new HardLinkResult(true, 1, ""), HardLinkInspector.Inspect(path));
        Link(Alias, path);
        Assert.Equal(new HardLinkResult(true, 2, ""), HardLinkInspector.Inspect(path));
        Assert.Equal(new HardLinkResult(true, 2, ""), HardLinkInspector.Inspect(Alias));
        var failure = HardLinkInspector.Inspect(Path.Combine(Repo, "missing"));
        Assert.False(failure.Success);
        Assert.Contains("OS error", failure.Reason);
        Assert.Contains(OperatingSystem.IsWindows() ? "File.OpenHandle" : OperatingSystem.IsLinux() ? "statx" : "stat", failure.Reason);
    }

    [Theory]
    [InlineData("replace", false, false, false)]
    [InlineData(null, false, false, false)]
    [InlineData("unknown", false, false, false)]
    [InlineData("section", false, false, false)]
    [InlineData("replace", true, false, false)]
    [InlineData("replace", false, true, false)]
    [InlineData("replace", false, false, true)]
    public async Task Changed_aliases_refuse_without_any_mutation(string? mode, bool inside, bool late, bool duplicate)
    {
        var path = Path.Combine(Repo, "é-文件.md");
        File.WriteAllText(path, "old\n");
        Link(inside ? Path.Combine(Repo, "alias.md") : Alias, path);
        WriteManifest(mode: mode, late: late, duplicate: duplicate);
        var before = Snapshot();
        var result = await Install();
        Assert.Equal(2, result.Exit);
        using var error = JsonDocument.Parse(result.Error);
        Assert.Equal("invalid_manifest", error.RootElement.GetProperty("code").GetString());
        Assert.Equal($"{Manifest}: entry {(late ? 1 : 0)} writes \"é-文件.md\", which has 2 hard links; replace it with an independent file before installing.",
            error.RootElement.GetProperty("message").GetString());
        Unchanged(before);
    }

    [Theory]
    [InlineData("create", "old\r\n")]
    [InlineData("replace", "new\r\n")]
    [InlineData("section", "<!-- BEGIN MUTHUR -->\r\nnew\r\n<!-- END MUTHUR -->\r\n")]
    public async Task No_op_modes_preserve_alias_bytes(string mode, string content)
    {
        var path = Path.Combine(Repo, "é-文件.md");
        File.WriteAllText(path, content);
        Link(Alias, path);
        WriteManifest(mode: mode);
        Assert.True(KitCommands.TryReadManifest(Manifest, Harness, Kit, Repo, out _, out var problem,
            _ => throw new InvalidOperationException("No-op must not inspect")), problem);
        Assert.Equal(0, (await Install()).Exit);
        Assert.Equal(content, File.ReadAllText(Alias));
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal("new\n", File.ReadAllText(Source));
    }

    [Theory]
    [InlineData(".gitignore", "bin/\n", false, false)]
    [InlineData(".gitignore", ".worktrees/\r\n", true, false)]
    [InlineData("muthur.project.json", "original project", true, false)]
    [InlineData("muthur.project.json", "original project", false, true)]
    public async Task Installer_files_follow_write_intent(string name, string content, bool allowed, bool explicitEntry)
    {
        var path = Path.Combine(Repo, name);
        File.WriteAllText(path, content);
        Link(Alias, path);
        WriteManifest(to: explicitEntry ? name : "new/nested/safe.md");
        var before = Snapshot();
        var result = await Install();
        Assert.Equal(allowed ? 0 : 2, result.Exit);
        if (!allowed)
        {
            using var error = JsonDocument.Parse(result.Error);
            var prefix = explicitEntry ? $"{Manifest}: entry 0" : "kit install";
            Assert.Equal("invalid_manifest", error.RootElement.GetProperty("code").GetString());
            Assert.Equal($"{prefix} writes \"{name}\", which has 2 hard links; replace it with an independent file before installing.",
                error.RootElement.GetProperty("message").GetString());
            Unchanged(before);
        }
        Assert.Equal(content, File.ReadAllText(Alias));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task Single_link_and_absent_files_install()
    {
        File.WriteAllText(Path.Combine(Repo, "é-文件.md"), "old");
        WriteManifest(late: true);
        Assert.Equal(0, (await Install()).Exit);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(Repo, "é-文件.md")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(Repo, "new/nested/safe.md")));
        Assert.True(File.Exists(Path.Combine(Repo, ".gitignore")));
        Assert.True(File.Exists(Path.Combine(Repo, "muthur.project.json")));
    }

    [Fact]
    public async Task Inward_directory_link_cannot_hide_hard_links()
    {
        var docs = Directory.CreateDirectory(Path.Combine(Repo, "docs")).FullName;
        var path = Path.Combine(docs, "file.md");
        File.WriteAllText(path, "old\n");
        Link(Alias, path);
        var inward = Path.Combine(Repo, "inward");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", inward, docs }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10000)) { process.Kill(true); throw new TimeoutException("Junction fixture"); }
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(inward, docs);
        directoryLinks.Add(inward);
        WriteManifest("inward/file.md");
        var result = await Install();
        Assert.Equal(2, result.Exit);
        using var error = JsonDocument.Parse(result.Error);
        Assert.Equal($"{Manifest}: entry 0 writes \"inward/file.md\", which has 2 hard links; replace it with an independent file before installing.",
            error.RootElement.GetProperty("message").GetString());
        Assert.Equal("old\n", File.ReadAllText(Alias));
        Assert.False(File.Exists(Path.Combine(Repo, ".gitignore")));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("zero")]
    [InlineData("throw")]
    public void Inspection_failures_are_attributed_and_do_not_escape(string kind)
    {
        File.WriteAllText(Path.Combine(Repo, "é-文件.md"), "old");
        WriteManifest(late: true);
        var before = Snapshot();
        var reason = kind == "zero" ? "native inspection returned zero hard links"
            : kind == "throw" ? "IOException: inspection denied" : "statx failed: OS error 38";
        Assert.False(KitCommands.TryReadManifest(Manifest, Harness, Kit, Repo, out var entries, out var problem,
            _ => kind == "throw" ? throw new IOException("inspection denied")
                : kind == "zero" ? new(true, 0, "") : HardLinkResult.Failure(reason)));
        Assert.Empty(entries);
        Assert.Equal($"{Manifest}: entry 1 writes \"é-文件.md\", but its hard-link count could not be determined: {reason}. No files were written.", problem);
        Unchanged(before);
    }

    [Fact]
    public void Unsupported_platform_or_abi_refuses_with_entry_attribution()
    {
        File.WriteAllText(Path.Combine(Repo, "é-文件.md"), "old");
        WriteManifest(late: true);
        var before = Snapshot();
        foreach (var reason in new[]
        {
            HardLinkInspector.PlatformFailure(OSPlatform.FreeBSD, Architecture.X64, true),
            HardLinkInspector.PlatformFailure(OSPlatform.Windows, Architecture.X86, true)
        })
        {
            Assert.NotNull(reason);
            Assert.False(KitCommands.TryReadManifest(Manifest, Harness, Kit, Repo, out var entries, out var problem,
                _ => HardLinkResult.Failure(reason)));
            Assert.Empty(entries);
            Assert.Equal($"{Manifest}: entry 1 writes \"é-文件.md\", but its hard-link count could not be determined: {reason}. No files were written.", problem);
            Unchanged(before);
        }
    }

    [Fact]
    public void Installer_owned_inspection_failure_refuses_before_earlier_safe_writes()
    {
        var ignore = Path.Combine(Repo, ".gitignore");
        File.WriteAllText(ignore, "bin/\r\n");
        WriteManifest(to: "new/nested/safe.md");
        var before = Snapshot();
        var inspected = new List<string>();
        Assert.False(KitCommands.TryReadManifest(Manifest, Harness, Kit, Repo, out var entries, out var problem,
            path =>
            {
                inspected.Add(path);
                return HardLinkResult.Failure("inspection denied");
            }));
        Assert.Equal([ignore], inspected);
        Assert.Empty(entries);
        Assert.Equal("kit install writes \".gitignore\", but its hard-link count could not be determined: inspection denied. No files were written.", problem);
        Unchanged(before);
    }

    [Fact]
    public void Pure_platform_abi_and_result_checks_do_not_claim_native_execution()
    {
        foreach (var platform in new[] { OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX })
        {
            Assert.Null(HardLinkInspector.PlatformFailure(platform, Architecture.X64, true));
            Assert.Null(HardLinkInspector.PlatformFailure(platform, Architecture.Arm64, true));
            Assert.Contains("unsupported architecture", HardLinkInspector.PlatformFailure(platform, Architecture.X86, true)!);
        }
        Assert.Contains("unsupported platform", HardLinkInspector.PlatformFailure(OSPlatform.FreeBSD, Architecture.X64, true)!);
        Assert.Contains("byte order", HardLinkInspector.PlatformFailure(OSPlatform.Linux, Architecture.Arm64, false)!);
        Assert.False(HardLinkInspector.StatxResult(0, 1).Success);
        Assert.Contains("STATX_NLINK", HardLinkInspector.StatxResult(0, 1).Reason);
        Assert.False(HardLinkInspector.StatxResult(4, 0).Success);
        Assert.Equal(new HardLinkResult(true, 2, ""), HardLinkInspector.StatxResult(4, 2));
        Assert.Equal("statx failed: OS error 38", HardLinkInspector.NativeFailure("statx", 38).Reason);
        Assert.False(HardLinkResult.FromCount(0).Success);
        Assert.Equal(52, Marshal.SizeOf<HardLinkInspector.WindowsInfo>());
        Assert.Equal(256, Marshal.SizeOf<HardLinkInspector.LinuxInfo>());
        Assert.Equal(144, Marshal.SizeOf<HardLinkInspector.DarwinInfo>());
        Assert.Equal(40, Marshal.OffsetOf<HardLinkInspector.WindowsInfo>("NumberOfLinks").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<HardLinkInspector.LinuxInfo>("NumberOfLinks").ToInt32());
        Assert.Equal(6, Marshal.OffsetOf<HardLinkInspector.DarwinInfo>("NumberOfLinks").ToInt32());
    }
}
