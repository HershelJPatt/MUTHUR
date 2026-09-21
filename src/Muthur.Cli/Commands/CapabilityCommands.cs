using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

public static class CapabilityCommands
{
    public static void AddTo(RootCommand root) => AddTo(root, new ProcessRunner());

    internal static void AddTo(RootCommand root, IProcessRunner processes)
    {
        var command = new Command("capability", "Inspect demonstrated assignment capabilities; production probes require shared admission.");
        root.Subcommands.Add(command);
        foreach (var name in new[] { "inspect", "probe" })
        {
            var spec = new Option<string>("--spec") { Required = true };
            var baseRef = new Option<string>("--base") { Required = true };
            var defaultBranch = new Option<string>("--default-branch") { Required = true };
            var harness = new Option<string>("--harness") { Required = true };
            var path = new Option<string>("--launch-path") { Required = true };
            var timeout = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 90 };
            var action = new Command(name, name == "inspect" ? "Read exact-identity evidence without mutation." : "Request one bounded probe; unavailable until shared admission is integrated.")
                { spec, baseRef, defaultBranch, harness, path };
            if (name == "probe") action.Options.Add(timeout);
            action.SetAction(async (parse, ct) =>
            {
                if (name == "probe")
                {
                    if (parse.GetValue(timeout) is < 1 or > 120)
                        return Output.Error("invalid_probe_timeout", "--timeout-seconds must be between 1 and 120.", ExitCodes.RuleViolation);
                    return Output.Error(parse.GetValue(path) == "worker-run" ? "capability_probe_admission_unavailable" : "capability_probe_unsupported",
                        "No probe started. Shared worker budget/capacity admission is unavailable; only worker-run has a bounded fixture engine. No model was invoked.", ExitCodes.RuleViolation);
                }
                try
                {
                    var repository = await processes.RunAsync("git", ["rev-parse", "--show-toplevel"], Environment.CurrentDirectory,
                        timeout: TimeSpan.FromSeconds(10), ct: ct);
                    if (!repository.Ok) return Output.Error("not_a_repository", "Run inside the assignment repository.", ExitCodes.RuleViolation);
                    var repo = Path.GetFullPath(repository.StdOut.Trim());
                    var assignment = await WorkerAssignment.ResolveAsync(processes, repo, parse.GetValue(baseRef)!, parse.GetValue(defaultBranch)!,
                        parse.GetValue(spec)!, "capability-inspect", repo, ct);
                    var frozen = await processes.RunAsync("git", ["cat-file", "blob", assignment.SpecBlob], repo,
                        timeout: TimeSpan.FromSeconds(10), ct: ct);
                    if (!frozen.Ok) return Output.Error("spec_unreadable", "Cannot read the pinned spec blob.", ExitCodes.RuleViolation);
                    var requirements = CapabilityRequirements.Parse(frozen.StdOut);
                    var launchPath = parse.GetValue(path)!;
                    var candidate = ReadCandidate(MuthurEnvironment.Home, parse.GetValue(harness)!, launchPath);
                    var common = await processes.RunAsync("git", ["rev-parse", "--git-common-dir"], repo,
                        timeout: TimeSpan.FromSeconds(10), ct: ct);
                    if (!common.Ok) return Output.Error("capability_identity_unknown", "Git common directory is unreadable.", ExitCodes.RuleViolation);
                    var git = launchPath == "worker-run" ? Path.GetFullPath(Path.Combine(repo, common.StdOut.Trim())) : null;
                    var request = WorkerCommands.RequestFor(candidate, repo, "", git,
                        [.. WorkerCommands.DefaultAllowed, .. WorkerCommands.ReadProject(repo).Allowed], Path.Combine(MuthurEnvironment.Home, "capability-inspect"));
                    if (launchPath is "conductor-validator" or "conductor-orchestrator")
                        request = request with
                        {
                            AllowedCommands = ["muthur*", "git*", "dotnet*", "pwsh*", "powershell*"],
                            DeniedCommands = ["git push*", "git merge*", "git rebase*", "git checkout main*", "git switch main*", "gh*"],
                        };
                    request = request with
                    {
                        GitEnvironment = SessionWorkspace.GitEnvironment(repo),
                        Capabilities = new(requirements, launchPath, Path.Combine(MuthurEnvironment.Home, "capabilities"), assignment.BaseCommit, repo),
                    };
                    var inspection = await new CapabilityEvaluator(processes).InspectAsync(Harnesses.Find(candidate.Harness), request, ct);
                    return Output.Emit(parse, new ApiResult(200, JsonSerializer.Serialize(inspection, CapabilityJsonContext.Default.CapabilityInspection)));
                }
                catch (WorkerDispatchException ex) { return Output.Error(ex.Code, ex.Message, ExitCodes.RuleViolation); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    return Output.Error("capability_identity_unknown", "Cannot read the effective catalog/configuration; no previous success was reused.", ExitCodes.RuleViolation);
                }
            });
            command.Subcommands.Add(action);
        }
    }

    private static HarnessCandidate ReadCandidate(string home, string harness, string path)
    {
        var catalog = Path.Combine(home, MuthurEnvironment.HarnessFile);
        if (new FileInfo(catalog).Length > 1_048_576) throw new IOException("Catalog exceeds read limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(catalog));
        var tier = path == "worker-run" ? "implementer" : "mastermind";
        foreach (var item in document.RootElement.GetProperty("tiers").GetProperty(tier).EnumerateArray())
        {
            if (item.GetProperty("harness").GetString() != harness) continue;
            return new(harness, item.GetProperty("model").GetString() ?? "",
                item.TryGetProperty("account", out var account) ? account.GetString() : null,
                item.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null);
        }
        throw new WorkerDispatchException("capability_candidate_unavailable", $"No '{harness}' candidate exists in the {tier} catalog.");
    }
}
