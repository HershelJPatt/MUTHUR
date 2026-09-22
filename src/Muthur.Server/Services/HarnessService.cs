using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>
/// Which harness/model/account staffs each tier, and which accounts are currently out of quota.
/// The catalog is a file the founder edits; it is re-read on every call so edits apply immediately.
/// </summary>
public sealed class HarnessService(Ledger ledger, MuthurOptions options)
{
    private string CatalogPath => Path.Combine(options.DataDir, MuthurEnvironment.HarnessFile);

    public void EnsureCatalogExists()
    {
        if (!File.Exists(CatalogPath)) File.WriteAllText(CatalogPath, HarnessDefaults.CatalogJson);
    }

    public Task<IReadOnlyList<TierDto>> TiersAsync(string? tier = null, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<TierDto>>(async (db, now) =>
        {
            var limits = await db.AccountLimits.Where(l => l.LimitedUntil > now).ToDictionaryAsync(l => l.Account, l => l.LimitedUntil, ct);
            var wanted = tier?.Trim().ToLowerInvariant();
            return ReadCatalog()
                .Where(t => wanted is null || t.Tier == wanted)
                .Select(t => new TierDto(t.Tier, t.Candidates.Select(c =>
                {
                    var until = c.Account is not null && limits.TryGetValue(c.Account, out var u) ? u : (DateTimeOffset?)null;
                    return new HarnessCandidateDto(c.Harness, c.Model, c.Account, until is not null, until, c.ReasoningEffort);
                }).ToList()))
                .ToList();
        }, ct);

    public Task ReportLimitAsync(Caller caller, AccountLimitRequest request, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        if (string.IsNullOrWhiteSpace(request.Account)) throw Fail.Rule("account_required", "Name the account that is out of quota.");
        return ledger.MutateAsync(caller, m => ApplyAsync(m, request.Account.Trim(), request.Until, ct), ct);
    }

    /// <summary>Automatic exhaustion never clears or shortens an existing account limit.</summary>
    internal static async Task ExhaustedAsync(Mutation m, string account, CancellationToken ct)
    {
        var existing = await m.Db.AccountLimits.SingleOrDefaultAsync(l => l.Account == account, ct);
        var until = m.Now.AddHours(1);
        if (existing?.LimitedUntil > until) return;
        await ApplyAsync(m, account, until, ct);
    }

    /// <summary>Shared with <see cref="AgentService"/>: an agent reporting itself limited also limits its account.</summary>
    public static async Task ApplyAsync(Mutation m, string account, DateTimeOffset? until, CancellationToken ct)
    {
        var existing = await m.Db.AccountLimits.SingleOrDefaultAsync(l => l.Account == account, ct);
        if (until is null || until <= m.Now)
        {
            if (existing is null) return;
            m.Db.AccountLimits.Remove(existing);
            m.Record("account.limit_cleared", payload: new { account });
            return;
        }
        if (existing is null)
        {
            existing = new AccountLimit { Account = account, ReportedBy = m.Caller.Name };
            m.Db.AccountLimits.Add(existing);
        }
        existing.LimitedUntil = until.Value;
        existing.ReportedBy = m.Caller.Name;
        existing.ReportedAt = m.Now;
        m.Record("account.limited", payload: new { account, until, by = m.Caller.Name });
    }

    public Task RecordWorkerRunAsync(Caller caller, WorkerRunReport report, CancellationToken ct = default)
    {
        caller.RequireIdentified();
        return ledger.MutateAsync(caller, async m =>
        {
            int? taskId = report.Task is { Length: > 0 } id ? (await TaskService.LoadAsync(m.Db, id, ct)).Id : null;
            var worker = $"{report.Harness}/{(report.Model.Length > 0 ? report.Model : "default")}";
            var seconds = report.DurationSeconds;
            // Absent, not null, when an orchestrator started the run itself: a parent key on every row would say
            // every run came from somewhere, and a tree is only readable while that stays false.
            object payload = report.Parent is { Length: > 0 } parent
                ? new { report.Tier, worker, report.Account, report.Branch, report.Unit, seconds, report.CostUsd, report.InputTokens, report.OutputTokens, parent,
                    report.RunId, report.Status, report.FailureKind, report.BaseCommit, report.HeadCommit, report.SpecBlob, report.ExitCode, report.Attempts, report.WorkKind, report.PolicyVersion, report.ReasoningEffort }
                : new { report.Tier, worker, report.Account, report.Branch, report.Unit, seconds, report.CostUsd, report.InputTokens, report.OutputTokens,
                    report.RunId, report.Status, report.FailureKind, report.BaseCommit, report.HeadCommit, report.SpecBlob, report.ExitCode, report.Attempts, report.WorkKind, report.PolicyVersion, report.ReasoningEffort };
            m.Record(report.Success ? "worker.finished" : "worker.failed", taskId, payload);
        }, ct);
    }

    private sealed record CatalogTier(string Tier, List<HarnessCandidate> Candidates);

    internal bool HasWorkerCandidate(WorkerAdmissionRequest request)
    {
        try
        {
            return ReadCatalog(create: false).Any(t => t.Tier == request.Tier && t.Candidates.Any(c =>
                c.Harness == request.Harness && c.Model == request.Model && c.Account == request.Account));
        }
        catch (MuthurException ex) when (ex.Code is "catalog_unreadable" or "catalog_invalid")
        { throw Fail.Rule("worker_candidate_unavailable", ex.Message); }
    }
    internal bool HasProbeCandidate(ProbeAdmissionRequest request)
    {
        try
        {
            return ReadCatalog(create: false).Any(t => t.Tier == request.Tier && t.Candidates.Any(c =>
                c.Harness == request.Harness && c.Model == request.Model && c.Account == request.Account));
        }
        catch (MuthurException ex) when (ex.Code is "catalog_unreadable" or "catalog_invalid")
        {
            throw Fail.Rule("probe_candidate_unavailable", ex.Message);
        }
    }

    /// <summary>Throws MuthurException and nothing else when the catalog cannot be used.</summary>
    private List<CatalogTier> ReadCatalog(bool create = true)
    {
        try
        {
            if (create) EnsureCatalogExists();
            else if (!File.Exists(CatalogPath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var tiers = new List<CatalogTier>();
            foreach (var tier in doc.RootElement.GetProperty("tiers").EnumerateObject())
            {
                var candidates = new List<HarnessCandidate>();
                foreach (var c in tier.Value.EnumerateArray())
                {
                    var harness = c.GetProperty("harness").GetString() ?? "";
                    var model = c.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
                    var account = c.TryGetProperty("account", out var ac) ? ac.GetString() : null;
                    var effort = c.TryGetProperty("reasoningEffort", out var re) ? re.GetString() : null;
                    // "enabled": false keeps a candidate in the file as a toggle without staffing anything on it.
                    var enabled = !c.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                    if (harness.Length > 0 && enabled) candidates.Add(new HarnessCandidate(harness, model, account, effort is { Length: > 0 } ? effort : null));
                }
                tiers.Add(new CatalogTier(tier.Name.ToLowerInvariant(), candidates));
            }
            return tiers;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Fail.Rule("catalog_unreadable", $"{CatalogPath} could not be read: {ex.Message}");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Fail.Rule("catalog_invalid", $"{CatalogPath} is not a valid harness catalog: {ex.Message}");
        }
    }
}
