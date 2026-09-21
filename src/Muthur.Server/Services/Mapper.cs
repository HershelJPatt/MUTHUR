using System.Text.Json;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

public static class Mapper
{
    public static ProjectDto ToDto(this Project p) =>
        new(p.Id, p.Key, p.Name, p.RepoPath, p.DefaultBranch, p.LandMode, p.RequiredValidators, p.CreatedAt, p.IngestSources);

    public static AgentDto ToDto(this Agent a, LeasePolicy leases, DateTimeOffset now, IReadOnlyList<string> roles, int openTasks) =>
        new(a.Id, a.Name, leases.StatusOf(a, now), a.LastHeartbeat, a.LimitedUntil, a.Summary, a.Harness, a.Model, a.Tier, a.Account, roles, openTasks, a.ConductorStaffed);

    /// <summary>Requires <see cref="WorkTask.Project"/> and <see cref="WorkTask.Owner"/> to be loaded.</summary>
    public static TaskDto ToDto(this WorkTask t, IReadOnlyList<ValidationDto>? validations = null) =>
        new(Wire.TaskId(t.Id),
            t.Project?.Key ?? "",
            t.Title,
            t.Body,
            t.State,
            t.Priority,
            t.Owner?.Name,
            t.ClaimExpires,
            t.SpecPath,
            t.Branch,
            t.PrUrl,
            t.ParentId is { } parent ? Wire.TaskId(parent) : null,
            t.CreatedAt,
            t.UpdatedAt,
            t.DoneAt,
            validations ?? [],
            t.AttendedReason,
            t.HoldReason,
            t.HoldBy,
            t.HoldExpires, t.DependsOn, t.DependencyReason, t.CurrentSubject?.ToDto(),
            t.ValidationInvalidationReason is not null ? "stale" : t.CurrentSubject is null ? "unknown" : "current",
            t.CurrentIntegrationCandidate?.ToDto());

    public static IntegrationCandidateDto ToDto(this IntegrationCandidate c) =>
        new(c.Id, c.AssignmentId, c.ProjectId, Wire.TaskId(c.TaskId), c.SubjectId,
            c.RepositoryPath, c.DefaultBranch, c.TargetSha, c.ImplementationSha, c.CandidateSha, c.TreeSha,
            JsonSerializer.Deserialize(c.RequiredChecksJson, MuthurJsonContext.Default.ValidationChecksDto)!,
            c.State, c.AssignedAgentId, c.LeaseExpires, c.Attempt, c.CreatedAt, c.StartedAt, c.CompletedAt,
            c.AlreadyIncluded, c.PromotionIntentAt, c.PromotedAt,
            c.EvidenceJson is null ? null : JsonSerializer.Deserialize(c.EvidenceJson, MuthurJsonContext.Default.IntegrationEvidenceDto),
            c.EvidenceSha256, c.FailureCode, c.FailureMessage,
            c.FailureJson is null ? null : JsonSerializer.Deserialize(c.FailureJson, MuthurJsonContext.Default.IntegrationFailureRequest));

    public static EventDto ToDto(this LedgerEvent e)
    {
        using var doc = JsonDocument.Parse(e.PayloadJson);
        return new EventDto(e.Seq, e.At, e.Actor, e.ActorModel, e.Type, e.TaskId is { } id ? Wire.TaskId(id) : null, doc.RootElement.Clone());
    }
}
