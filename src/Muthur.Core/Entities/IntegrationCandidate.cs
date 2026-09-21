namespace Muthur.Core.Entities;

public sealed class IntegrationCandidate
{
    public Guid Id { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public int TaskId { get; set; }
    public WorkTask? Task { get; set; }
    public Guid SubjectId { get; set; }
    public ValidationSubject? Subject { get; set; }
    public required string RepositoryPath { get; set; }
    public required string DefaultBranch { get; set; }
    public required string TargetSha { get; set; }
    public required string ImplementationSha { get; set; }
    public string? CandidateSha { get; set; }
    public string? TreeSha { get; set; }
    public required string RequiredChecksJson { get; set; }
    public string State { get; set; } = "assigned";
    public Guid? AssignedAgentId { get; set; }
    public Agent? AssignedAgent { get; set; }
    public DateTimeOffset LeaseExpires { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool AlreadyIncluded { get; set; }
    public DateTimeOffset? PromotionIntentAt { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
    public string? EvidenceJson { get; set; }
    public string? EvidenceSha256 { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string? FailureJson { get; set; }
}
