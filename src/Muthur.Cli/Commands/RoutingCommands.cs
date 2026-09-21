using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

public static class RoutingCommands
{
    public static void AddTo(RootCommand root)
    {
        var group = new Command("routing", "Advisory routing reports; never launches workers or changes policy.");
        root.Subcommands.Add(group);
        var showTask = new Option<string>("--task") { Required = true };
        var show = new Command("show", "Read immutable recommendation history.") { showTask };
        show.SetAction(async (p, ct) => Output.Emit(p, await HubClient.For(p).GetAsync("/api/v1/routing/" + Uri.EscapeDataString(p.GetValue(showTask)!), ct)));
        group.Subcommands.Add(show);
        foreach (var name in new[] { "report", "record" })
        {
            var task = new Option<string>("--task") { Required = true };
            var spec = new Option<string>("--spec") { Required = true };
            var baseRef = new Option<string>("--base") { Required = true };
            var defaultBranch = new Option<string>("--default-branch") { Required = true };
            var tier = new Option<string>("--tier") { DefaultValueFactory = _ => "implementer" };
            var hours = new Option<int>("--hours") { DefaultValueFactory = _ => 168 };
            var command = new Command(name, name == "report" ? "Inspect suitability without mutations or probes." : "Record this owner-generated observation snapshot.") { task, spec, baseRef, defaultBranch, tier, hours };
            command.SetAction(async (p, ct) =>
            {
                if (p.GetValue(tier) != "implementer") return Output.Error("routing_tier_unsupported", "Only implementer recommendations are supported; validator independence uses its existing route.", 2);
                if (p.GetValue(hours) is < 1 or > 720) return Output.Error("routing_window", "Hours must be 1..720.", 2);
                try
                {
                    var process = new ProcessRunner();
                    var git = await process.RunAsync("git", ["rev-parse", "--show-toplevel"], Environment.CurrentDirectory, ct: ct);
                    if (!git.Ok) return Output.Error("not_a_repository", git.Message, 2);
                    var repo = Path.GetFullPath(git.StdOut.Trim());
                    var assignment = await WorkerAssignment.ResolveAsync(process, repo, p.GetValue(baseRef)!, p.GetValue(defaultBranch)!, p.GetValue(spec)!, "routing-inspection", repo, ct);
                    var frozen = await process.RunAsync("git", ["cat-file", "blob", assignment.SpecBlob], repo, ct: ct);
                    if (!frozen.Ok) return Output.Error("spec_unreadable", frozen.Message, 2);
                    var kind = RoutingPolicy.WorkKind(frozen.StdOut);
                    var requirements = CapabilityRequirements.Parse(frozen.StdOut);
                    var hub = HubClient.For(p);
                    var catalog = await hub.GetAsync(Routes.Tiers + "?tier=implementer", ct);
                    if (!catalog.IsSuccess) return Output.Emit(p, catalog);
                    var historyResult = await hub.GetAsync("/api/v1/routing/history?hours=" + p.GetValue(hours), ct);
                    if (!historyResult.IsSuccess) return Output.Emit(p, historyResult);
                    var history = JsonSerializer.Deserialize(historyResult.Body, MuthurJsonContext.Default.RoutingHistory)!;
                    var tiers = JsonSerializer.Deserialize(catalog.Body, MuthurJsonContext.Default.IReadOnlyListTierDto)!;
                    var common = await process.RunAsync("git", ["rev-parse", "--git-common-dir"], repo, ct: ct);
                    if (!common.Ok) return Output.Error("routing_identity", common.Message, 2);
                    var project = await WorkerCommands.ReadPinnedProjectAsync(process, repo, assignment.BaseCommit, ct);
                    var rows = new List<RoutingCandidate>();
                    foreach (var c in tiers.Where(t => t.Tier == "implementer").SelectMany(t => t.Candidates))
                    {
                        var candidate = new HarnessCandidate(c.Harness, c.Model, c.Account, c.ReasoningEffort);
                        var request = WorkerCommands.RequestFor(candidate, repo, "", Path.GetFullPath(Path.Combine(repo, common.StdOut.Trim())),
                            [.. WorkerCommands.DefaultAllowed, .. project.Allowed], Path.Combine(MuthurEnvironment.Home, "capability-scratch")) with
                        {
                            GitEnvironment = SessionWorkspace.GitEnvironment(repo),
                            Capabilities = new(requirements, "worker-run", Path.Combine(MuthurEnvironment.Home, "capabilities"), assignment.BaseCommit, repo),
                        };
                        var inspection = await new CapabilityEvaluator(process).InspectAsync(Harnesses.Find(c.Harness), request, ct);
                        var runs = history.Runs.Where(r => r.Tier == "implementer" && r.Worker == c.Harness + "/" + c.Model && r.Account == c.Account
                            && r.ReasoningEffort == c.ReasoningEffort && r.WorkKind == kind && kind != "unknown").ToList();
                        runs = runs.Where(r => r.RunId is null).Concat(runs.Where(r => r.RunId is not null).GroupBy(r => r.RunId).Select(g => g.MaxBy(r => r.At)!)).ToList();
                        rows.Add(new(c.Harness, c.Model, c.Account, c.ReasoningEffort, rows.Count, inspection.Match.Allowed, !c.Limited,
                            requirements.Count == 0 ? "Legacy unchecked; no demonstrated capabilities required." : JsonSerializer.Serialize(inspection, CapabilityJsonContext.Default.CapabilityInspection),
                            0, null, null, null, null, null, runs.Count(r => r.Success), runs.Count(r => !r.Success),
                            runs.Select(r => $"task:{r.Task};run:{r.RunId ?? "unknown"};revision:{r.HeadCommit ?? "unknown"}").ToList(),
                            ["Independent per-worker correctness, false approvals, actual starts and comparable end-to-end outcomes are unknown; reported success is not product completion."]));
                    }
                    var chosen = RoutingPolicy.Recommend(rows);
                    var report = new RoutingReport(1, "measured-quality-v1", DateTimeOffset.UtcNow, p.GetValue(task)!, assignment.BaseCommit, assignment.SpecBlob, kind,
                        history.From, history.To, history.LedgerBoundary, requirements, rows, chosen.Position, chosen.Reason,
                        history.ReadyTasks.FirstOrDefault()?.Id, history.PendingValidation,
                        ["Live shared capacity admission", "Daily task budget", "Account availability", "Fresh capability identity", "Independent validation through the validator route"]);
                    return name == "record" ? Output.Emit(p, await hub.PostAsync("/api/v1/routing/" + Uri.EscapeDataString(report.Task), report, MuthurJsonContext.Default.RoutingReport, ct))
                        : Output.Emit(p, new ApiResult(200, JsonSerializer.Serialize(report, MuthurJsonContext.Default.RoutingReport)));
                }
                catch (WorkerDispatchException ex) { return Output.Error(ex.Code, ex.Message, 2); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return Output.Error("routing_unavailable", ex.Message, 2); }
            });
            group.Subcommands.Add(command);
        }
    }
}
