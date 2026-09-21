using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>Visual approval binds committed artifacts, the frozen specification, and independent interaction evidence.</summary>
public sealed class DesignService(Ledger ledger, ITaskLander lander)
{
    private static string Key(int task) => "design.T-" + task;
    private static void Text(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw Fail.Rule("design_input", "Design descriptions and evidence must contain 1..16000 characters.");
    }
    internal static async Task<DesignDto?> ReadAsync(MuthurDb db, WorkTask task, CancellationToken ct)
    {
        var row = await db.Meta.SingleOrDefaultAsync(m => m.Key == Key(task.Id), ct);
        if (row is null) return null;
        var value = JsonSerializer.Deserialize(row.Value, MuthurJsonContext.Default.DesignDto)!;
        var approval = value.Approvals.LastOrDefault(a => a.Revision == value.Revision);
        var status = approval is null ? "pending" : approval.SpecSha256 != task.SpecSha256 ? "stale" : approval.Approved ? "approved" : "rejected";
        return value with { ApprovalStatus = status };
    }
    private static async Task Save(Mutation m, WorkTask task, DesignDto design, string action, CancellationToken ct)
    {
        var row = await m.Db.Meta.SingleOrDefaultAsync(r => r.Key == Key(task.Id), ct);
        var value = JsonSerializer.Serialize(design, MuthurJsonContext.Default.DesignDto);
        if (row is null) m.Db.Meta.Add(new MetaEntry { Key = Key(task.Id), Value = value }); else row.Value = value;
        m.Record("design." + action, task.Id, new { design.Revision, sha256 = design.Revisions[^1].Sha256, design.ApprovalStatus });
    }
    private static void Revision(DesignDto? design, int expected)
    {
        if ((design?.Revision ?? 0) != expected) throw Fail.Conflict("design_revision", "Design revision changed.");
    }
    public Task<DesignDto?> GetAsync(string id, CancellationToken ct = default) => ledger.ReadAsync(async (db, _) => await ReadAsync(db, await TaskService.LoadAsync(db, id, ct), ct), ct);

    public Task<DesignDto> WriteAsync(Caller caller, string id, WriteDesignRequest request, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        var task = await TaskService.LoadAsync(m.Db, id, ct);
        caller.RequireIdentified(); TaskService.RequireOwnerOrFounder(task, caller);
        var old = await ReadAsync(m.Db, task, ct); Revision(old, request.ExpectedRevision);
        var d = request.Definition;
        if (d is null || d.Artifacts is null || d.Artifacts.Count is < 2 or > 30) throw Fail.Rule("design_input", "Provide a bounded before/after artifact set.");
        Text(d.Summary); Text(d.Constraints); Text(d.Accessibility); Text(d.Acceptance);
        if (!d.Artifacts.Any(a => a.Role == "before") || !d.Artifacts.Any(a => a.Role == "after")
            || new[] { "loading", "empty", "error", "success", "narrow" }.Except(d.Artifacts.Where(a => a.Role == "after").Select(a => a.State)).Any())
            throw Fail.Rule("design_states", "After artifacts must cover loading, empty, error, success and narrow layouts, with a before reference.");
        foreach (var artifact in d.Artifacts)
        {
            Text(artifact.Name); Text(artifact.State);
            if (artifact.Blob is null || artifact.Blob.Length is not (40 or 64) || !artifact.Blob.All(char.IsAsciiHexDigit))
                throw Fail.Rule("design_input", "Artifacts require an immutable Git blob identity.");
            if (artifact.Role is not ("before" or "after")) throw Fail.Rule("design_input", "Artifact role must be before or after.");
            try { VerificationFiles.Relative(artifact.Path); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { throw Fail.Rule("design_input", "Artifact path must be repository-relative."); }
            if (await lander.ArtifactBlobAsync(task.Project!, artifact.Commit, artifact.Path, ct) != artifact.Blob)
                throw Fail.Rule("design_artifact_changed", "Artifact is missing or does not match its immutable Git blob.");
        }
        var hash = Validations.Hash(JsonSerializer.Serialize(d, MuthurJsonContext.Default.DesignDefinition));
        var number = request.ExpectedRevision + 1;
        var design = new DesignDto(id, number, [.. old?.Revisions ?? [], new(number, hash, d, caller.Name, m.Now)], old?.Approvals ?? [], old?.Checks ?? [], "pending");
        if (task.CurrentSubject is not null) task.ValidationInvalidationReason = "The visual design changed; approve the current revision and resubmit.";
        await Save(m, task, design, "edited", ct);
        return design;
    }, ct);

    public Task<DesignDto> ApproveAsync(Caller caller, string id, ApproveDesignRequest request, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        if (!caller.IsFounder || caller.Name != "founder") throw Fail.Unauthorized("Visual preference approval requires the founder.");
        Text(request.Reason);
        var task = await TaskService.LoadAsync(m.Db, id, ct);
        var design = await ReadAsync(m.Db, task, ct) ?? throw Fail.NotFound("Design", id);
        Revision(design, request.ExpectedRevision);
        var revision = design.Revisions[^1];
        if (task.SpecSha256 is null || task.SpecPath is null || request.SpecCommit is null
            || request.SpecCommit.Length is not (40 or 64) || !request.SpecCommit.All(char.IsAsciiHexDigit))
            throw Fail.Rule("design_spec", "Attach a frozen spec and name its immutable commit before approving the design.");
        var spec = await lander.ReadFileAsync(task.Project!, request.SpecCommit, task.SpecPath, ct);
        if (spec is null || Validations.Hash(spec) != task.SpecSha256 || !SpecNames(spec, revision.Sha256))
            throw Fail.Rule("design_spec", "The frozen spec must contain an exact standalone design: <sha256> reference.");
        design = design with { ApprovalStatus = request.Approved ? "approved" : "rejected",
            Approvals = [.. design.Approvals, new(design.Revision, revision.Sha256, task.SpecSha256, request.Approved, caller.Name, m.Now, request.Reason)] };
        if (task.CurrentSubject is not null) task.ValidationInvalidationReason = "Visual approval changed; obtain a fresh validation subject.";
        await Save(m, task, design, "reviewed", ct);
        return design;
    }, ct);

    private static bool SpecNames(string spec, string sha) => spec.Split('\n').Any(l => l.Trim() == "design: " + sha);
    internal static async Task<string?> ApprovedHashAsync(MuthurDb db, WorkTask task, string? spec, CancellationToken ct)
    {
        var design = await ReadAsync(db, task, ct);
        if (design is null) return null;
        if (design.ApprovalStatus != "approved" || spec is not null && !SpecNames(spec, design.Revisions[^1].Sha256))
            throw Fail.Rule("design_approval_required", "Approve the current visual design bound to the frozen specification before validation.");
        return design.Revisions[^1].Sha256;
    }

    public Task<DesignDto> CheckAsync(Caller caller, string id, CheckDesignRequest request, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        caller.RequireIdentified(); Text(request.VisualEvidence); Text(request.InteractionEvidence);
        var task = await TaskService.LoadAsync(m.Db, id, ct);
        await Validations.RequireCurrentAsync(m.Db, task, lander, ct);
        if (task.CurrentSubjectId != request.Subject) throw Fail.Conflict("design_subject", "The validation subject changed.");
        if (!caller.IsFounder && (caller.AgentId == task.OwnerAgentId || !await m.Db.RoleHolds.AnyAsync(h => h.AgentId == caller.AgentId
            && task.Project!.RequiredValidators.Contains(h.RoleKey) && h.LeaseExpires > m.Now, ct))) throw Fail.Unauthorized("An independent current validator must record the comparison.");
        var design = await ReadAsync(m.Db, task, ct) ?? throw Fail.NotFound("Design", id);
        Revision(design, request.ExpectedRevision);
        design = design with { Checks = [.. design.Checks, new(design.Revision, request.Subject, task.CurrentSubject!.ImplementationSha,
            request.Matches, request.VisualEvidence, request.InteractionEvidence, caller.Name, m.Now)] };
        await Save(m, task, design, "checked", ct);
        return design;
    }, ct);

    internal static async Task RequireComparisonAsync(MuthurDb db, WorkTask task, string actor, CancellationToken ct)
    {
        var design = await ReadAsync(db, task, ct);
        if (design is null) return;
        var check = design.Checks.LastOrDefault(c => c.Actor == actor && c.Revision == design.Revision && c.Subject == task.CurrentSubjectId);
        if (check is null || !check.Matches) throw Fail.Rule("design_comparison_required", "Record a matching visual comparison and a separate interaction check for this exact subject before passing validation.");
    }
}
