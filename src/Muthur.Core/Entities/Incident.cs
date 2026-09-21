namespace Muthur.Core.Entities;

public sealed class Incident
{
    public int Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public required string Title { get; set; }
    public string State { get; set; } = "suspected";
    public required string Signature { get; set; }
    public required string ExecutionPath { get; set; }
    public required string Configuration { get; set; }
    public int ConditionVersion { get; set; } = 1;
    public string Diagnosis { get; set; } = "";
    public string Workaround { get; set; } = "";
    public string AuthorizationReference { get; set; } = "";
    public required string RecoveryCondition { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class IncidentObservation
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public int TaskId { get; set; }
    public string? RunId { get; set; }
    public required string Evidence { get; set; }
    public required string Signature { get; set; }
    public required string ExecutionPath { get; set; }
    public required string Configuration { get; set; }
    public int ConditionVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Actor { get; set; }
    public bool Active { get; set; } = true;
    public string? UnlinkReason { get; set; }
}

public sealed class IncidentSuppression
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public int TaskId { get; set; }
    public WorkTask? Task { get; set; }
    public required string Assignment { get; set; }
    public int ConditionVersion { get; set; }
    public int EvidenceObservationId { get; set; }
    public IncidentObservation? EvidenceObservation { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
