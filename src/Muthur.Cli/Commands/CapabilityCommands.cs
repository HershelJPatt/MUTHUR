using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

public static class CapabilityCommands
{
    public static void AddTo(RootCommand root) => AddTo(root, new CapabilityProcessRunner());

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
            var task = new Option<string>("--task") { Required = true };
            var action = new Command(name, name == "inspect" ? "Read exact-identity evidence without mutation." : "Request one bounded probe through shared task budget/capacity admission.")
                { spec, baseRef, defaultBranch, harness, path };
            if (name == "probe") { action.Options.Add(timeout); action.Options.Add(task); }
            action.SetAction(async (parse, ct) =>
            {
                if (name == "probe")
                {
                    if (parse.GetValue(timeout) is < 1 or > 120)
                        return Output.Error("invalid_probe_timeout", "--timeout-seconds must be between 1 and 120.", ExitCodes.RuleViolation);
                    if (parse.GetValue(path) != "worker-run")
                        return Output.Error("capability_probe_unsupported", "Only worker-run supports probing; no admission or model start attempted.", ExitCodes.RuleViolation);
                }
                using var budget = name == "probe" ? new CancellationTokenSource(TimeSpan.FromSeconds(parse.GetValue(timeout))) : null;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, budget?.Token ?? CancellationToken.None);
                ct = lifetime.Token;
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
                    HarnessCandidate candidate;
                    if (name == "probe")
                    {
                        var catalog = await HubClient.For(parse).GetAsync(Routes.Tiers + "?tier=implementer", ct);
                        if (!catalog.IsSuccess) return Output.Emit(parse, catalog);
                        var tiers = JsonSerializer.Deserialize(catalog.Body, MuthurJsonContext.Default.IReadOnlyListTierDto) ?? [];
                        var candidates = WorkerCommands.Candidates(tiers, parse.GetValue(harness));
                        if (candidates.Count != 1)
                            return Output.Error("capability_candidate_unavailable", "Probe requires exactly one available implementer catalog candidate matching --harness.", ExitCodes.RuleViolation);
                        candidate = candidates[0];
                    }
                    else candidate = ReadCandidate(MuthurEnvironment.Home, parse.GetValue(harness)!, launchPath);
                    var common = await processes.RunAsync("git", ["rev-parse", "--git-common-dir"], repo,
                        timeout: TimeSpan.FromSeconds(10), ct: ct);
                    if (!common.Ok) return Output.Error("capability_identity_unknown", "Git common directory is unreadable.", ExitCodes.RuleViolation);
                    var git = launchPath == "worker-run" ? Path.GetFullPath(Path.Combine(repo, common.StdOut.Trim())) : null;
                    var project = await WorkerCommands.ReadPinnedProjectAsync(processes, repo, assignment.BaseCommit, ct);
                    var request = WorkerCommands.RequestFor(candidate, repo, "", git,
                        [.. WorkerCommands.DefaultAllowed, .. project.Allowed], Path.Combine(MuthurEnvironment.Home, "capability-scratch"));
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
                    if (name == "probe")
                    {
                        var result = await new CapabilityProbe(processes, new ProbeAdmissionClient(HubClient.For(parse)))
                            .RunAsync(parse.GetValue(task)!, candidate, request, TimeSpan.FromSeconds(parse.GetValue(timeout)), ct);
                        return Output.Emit(parse, new ApiResult(result.Code is null ? 200 : 422,
                            JsonSerializer.Serialize(result, CapabilityJsonContext.Default.CapabilityProbeResult)));
                    }
                    var inspection = await new CapabilityEvaluator(processes).InspectAsync(Harnesses.Find(candidate.Harness), request, ct);
                    return Output.Emit(parse, new ApiResult(200, JsonSerializer.Serialize(inspection, CapabilityJsonContext.Default.CapabilityInspection)));
                }
                catch (WorkerDispatchException ex) { return Output.Error(ex.Code, ex.Message, ExitCodes.RuleViolation); }
                catch (OperationCanceledException) { return Output.Error("capability_probe_cancelled", "Capability operation exceeded its budget or was cancelled.", ExitCodes.RuleViolation); }
                catch (InvalidOperationException ex) when (name == "probe")
                { return Output.Error("capability_probe_admission_failed", ex.Message, ExitCodes.RuleViolation); }
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
        var matches = document.RootElement.GetProperty("tiers").GetProperty(tier).EnumerateArray()
            .Where(item => item.GetProperty("harness").GetString() == harness).ToArray();
        if (matches.Length != 1)
            throw new WorkerDispatchException("capability_candidate_unavailable", $"Exactly one '{harness}' candidate must exist in the {tier} catalog.");
        foreach (var item in matches)
        {
            if (item.GetProperty("harness").GetString() != harness) continue;
            return new(harness, item.GetProperty("model").GetString() ?? "",
                item.TryGetProperty("account", out var account) ? account.GetString() : null,
                item.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null);
        }
        throw new WorkerDispatchException("capability_candidate_unavailable", $"No '{harness}' candidate exists in the {tier} catalog.");
    }
}
