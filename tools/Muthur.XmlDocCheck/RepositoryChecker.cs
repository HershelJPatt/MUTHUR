using System.Diagnostics;
using System.Text;

namespace Muthur.XmlDocCheck;

public interface IGitRunner
{
    string Run(string root, params string[] arguments);
}

public sealed class GitRunner : IGitRunner
{
    public string Run(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-c", $"safe.directory={Path.GetFullPath(root).Replace('\\', '/')}", "-C", root }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Git timed out.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git failed: {error.Result.Trim()}");
        return output.Result;
    }
}

public static class RepositoryChecker
{
    public static IReadOnlyList<SummaryDiagnostic> Scan(string root, IGitRunner? git = null)
    {
        git ??= new GitRunner();
        root = git.Run(Path.GetFullPath(root), "rev-parse", "--show-toplevel").TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Git returned no repository root.");
        var paths = git.Run(root, "ls-files", "-z", "--", "src", "tests")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
        var diagnostics = new List<SummaryDiagnostic>();
        foreach (var path in paths.Where(SourceChecker.IncludesPath))
            diagnostics.AddRange(SourceChecker.Analyze(path, File.ReadAllText(Path.Combine(root, path))));
        return diagnostics;
    }
}
