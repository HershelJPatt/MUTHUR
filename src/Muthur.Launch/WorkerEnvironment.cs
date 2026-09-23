using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Muthur.Launch;

/// <summary>One thing the worker's sandbox identity cannot do, and the command that would let it.</summary>
public sealed record EnvironmentFinding(string Harness, string Identity, string Path, string Problem, string Fix)
{
    public string Message =>
        $"The {Harness} sandbox runs as '{Identity}', which cannot read {Path}: {Problem}. Starting a model would spend a session " +
        $"to discover this and report blocked. Grant it once from an administrator shell: {Fix}";
}

/// <summary>
/// What a worker's sandbox identity must be able to reach before a model is started for it, checked without a model.
/// <para>
/// Read off the ledger rather than imagined: on 09-21 about half of the implementer runs that came back
/// <c>blocked</c> had done their assignment check, found it clean, and then failed <c>dotnet build</c> because the
/// Codex sandbox user could not read the user's NuGet configuration. Every one of those was a full cold start spent
/// to learn a fact an ACL listing gives in a millisecond. The check is deliberately narrow — exactly the paths those
/// reports named — so that it refuses only what is known to fail, and says what to run to fix it.
/// </para>
/// </summary>
public static class WorkerEnvironment
{
    /// <summary>The local group Codex's Windows sandbox users belong to.</summary>
    public const string CodexSandboxGroup = "CodexSandboxUsers";

    /// <summary>Replaceable for tests: whether the named identity can read the path, or null when it cannot be told.</summary>
    public static Func<string, string, bool?> Readable { get; set; } = (path, group) => OperatingSystem.IsWindows() ? CanRead(path, group) : null;

    /// <summary>The paths the sandbox identity must read to restore a .NET project; the ones the blocked reports named.</summary>
    public static IReadOnlyList<string> RequiredReadable()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(appData is { Length: > 0 } ? appData : Path.Combine(profile, "AppData", "Roaming"), "NuGet", "NuGet.Config"),
            Path.Combine(profile, ".nuget", "packages"),
        ];
    }

    /// <summary>
    /// Why a worker on this harness would fail before it built anything, or null when nothing known is wrong.
    /// Only the Codex harnesses on Windows have a sandbox identity of their own; everywhere else the worker is the
    /// launching user and there is nothing to check.
    /// </summary>
    public static EnvironmentFinding? Check(string harness, IReadOnlyList<string>? paths = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!harness.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var path in paths ?? RequiredReadable())
        {
            if (!File.Exists(path) && !Directory.Exists(path)) continue;   // nothing there for the restore to want
            if (Readable(path, CodexSandboxGroup) == false)
            {
                // Granted on the containing directory for a file, so the whole NuGet folder (config plus any fallback
                // folders beside it) becomes readable in one command rather than one per file.
                var grantOn = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
                return new EnvironmentFinding(harness, CodexSandboxGroup, path, "no access-control entry grants that group read access",
                    $"icacls \"{grantOn}\" /grant \"{CodexSandboxGroup}:(OI)(CI)(RX)\"");
            }
        }
        return null;
    }

    /// <summary>
    /// Splits the candidates into those a worker can be started on and, for the rest, the attempt the report
    /// carries in their place: never started, failure kind <c>environment</c>, and the fix in the message.
    /// </summary>
    public static (IReadOnlyList<HarnessCandidate> Usable, IReadOnlyList<WorkerAttempt> Refused) Partition(IReadOnlyList<HarnessCandidate> candidates)
    {
        var usable = new List<HarnessCandidate>();
        var refused = new List<WorkerAttempt>();
        var findings = new Dictionary<string, EnvironmentFinding?>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!findings.TryGetValue(candidate.Harness, out var finding))
                findings[candidate.Harness] = finding = Check(candidate.Harness);
            if (finding is null) usable.Add(candidate);
            else refused.Add(new WorkerAttempt(candidate, new WorkerOutcome(false, "STATUS: blocked\nNOTES: " + finding.Message, false),
                TimeSpan.Zero, Started: false, FailureKind: "environment"));
        }
        return (usable, refused);
    }

    [SupportedOSPlatform("windows")]
    private static bool? CanRead(string path, string group)
    {
        try
        {
            var rules = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(NTAccount))
                : new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(NTAccount));
            var allowed = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                var name = rule.IdentityReference.Value;
                var slash = name.LastIndexOf('\\');
                if (!string.Equals(slash < 0 ? name : name[(slash + 1)..], group, StringComparison.OrdinalIgnoreCase)) continue;
                var reads = (rule.FileSystemRights & FileSystemRights.ReadData) != 0;
                if (rule.AccessControlType == AccessControlType.Deny && reads) return false;
                if (rule.AccessControlType == AccessControlType.Allow && reads) allowed = true;
            }
            return allowed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;   // cannot tell; refusing on a guess would be the wrong kind of certainty
        }
    }
}
