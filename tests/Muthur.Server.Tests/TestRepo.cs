using System.Diagnostics;

namespace Muthur.Server.Tests;

/// <summary>A throwaway git repository with a `main` branch and one commit.</summary>
public sealed class TestRepo : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "muthur-tests", "repo-" + Guid.NewGuid().ToString("n"));

    public TestRepo()
    {
        Directory.CreateDirectory(Path);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Write("README.md", "# test\n");
        Commit("initial");
    }

    public void Write(string file, string content)
    {
        var target = System.IO.Path.Combine(Path, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    /// <summary>Writes `specs/&lt;id&gt;.md` with a heading naming that task, which `task spec` requires.</summary>
    public string WriteSpec(string taskId = "T-1")
    {
        var file = $"specs/{taskId}.md";
        Write(file, $"# {taskId} — a spec for the test\n");
        return file;
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Creates <paramref name="branch"/> from main with one file committed, then returns to main.</summary>
    public void BranchWithFile(string branch, string file, string content)
    {
        Git("checkout", "-q", "-b", branch, "main");
        Write(file, content);
        Commit($"{branch}: {file}");
        Git("checkout", "-q", "main");
    }

    public string Git(params string[] arguments) => Run(Path, arguments);

    public static string Run(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal); // git marks objects read-only
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
