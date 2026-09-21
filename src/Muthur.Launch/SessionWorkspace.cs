using System.Globalization;

namespace Muthur.Launch;

/// <summary>Per-attempt outputs and exact, process-local Git trust for a launcher-selected repository.</summary>
public static class SessionWorkspace
{
    public static WorkerRequest ForAttempt(WorkerRequest request, string runId)
    {
        var scratch = Path.Combine(request.ScratchDirectory, runId);
        Directory.CreateDirectory(scratch);
        return request with { ScratchDirectory = scratch, GitEnvironment = request.RequireRepository
            ? GitEnvironment(request.WorkingDirectory) : null };
    }

    public static IReadOnlyDictionary<string, string> GitEnvironment(string repository,
        Func<string, string?>? inherited = null)
    {
        var root = Path.GetFullPath(repository).Replace('\\', '/');
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return new Dictionary<string, string>();
        inherited ??= Environment.GetEnvironmentVariable;
        var raw = inherited("GIT_CONFIG_COUNT");
        var count = raw is null ? 0 : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed <= 10000
            ? parsed : throw new InvalidOperationException("Invalid inherited GIT_CONFIG_COUNT.");
        var values = new Dictionary<string, string>();
        // Preserve existing overrides, including in harnesses that filter inherited environment variables.
        for (var i = 0; i < count; i++)
        {
            foreach (var part in new[] { "KEY", "VALUE" })
            {
                var key = $"GIT_CONFIG_{part}_{i}";
                values[key] = inherited(key) ?? throw new InvalidOperationException($"Missing inherited {key}.");
            }
        }
        values[$"GIT_CONFIG_KEY_{count}"] = "safe.directory";
        values[$"GIT_CONFIG_VALUE_{count}"] = root;
        values["GIT_CONFIG_COUNT"] = (count + 1).ToString(CultureInfo.InvariantCulture);
        return values;
    }

    internal static string? FailureKind(ProcessResult result, WorkerOutcome outcome) =>
        outcome.RateLimited ? "quota" : result.ExitCode == 124 ? "timeout" : !result.Ok ? "process_exit" :
        !outcome.Success ? "missing_or_failed_report" : null;
}
