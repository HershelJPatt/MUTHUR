namespace Muthur.Contracts;

public sealed record TaskUnitDefinition(string Id, IReadOnlyList<string> Dependencies, IReadOnlyList<string> RequiredChecks);
public sealed record DefineTaskUnitsRequest(long ExpectedRevision, string SpecBlob, string IntegrationBranch, IReadOnlyList<TaskUnitDefinition> Units);
public sealed record TaskUnitCheck(string Name, int ExitCode, string EvidencePath, string EvidenceCommit);
public sealed record TaskUnitCheckpointRequest(long ExpectedRevision, string UnitId, Guid AttemptId, string Action,
    string? BaseCommit = null, string? OutputBranch = null, string? OutputCommit = null,
    IReadOnlyList<TaskUnitCheck>? Checks = null, string? ReviewEvidencePath = null, string? ReviewEvidenceCommit = null,
    string? NextAction = null, string? Reason = null);

public sealed record TaskUnitAttempt(Guid AttemptId, string BaseCommit, string OutputBranch,
    string? OutputCommit, string ReportedState, string ReviewState, string? IntegrationCommit,
    IReadOnlyList<TaskUnitCheck> Checks, string? ReviewEvidencePath, string? ReviewEvidenceCommit,
    string NextAction, IReadOnlyDictionary<string, Guid> DependencyAttempts,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string ReportingActor);

public sealed record TaskUnit(string Id, IReadOnlyList<string> Dependencies, IReadOnlyList<string> RequiredChecks,
    TaskUnitAttempt? Attempt = null, string? InvalidationReason = null);
public sealed record TaskUnitGraph(int SchemaVersion, long Revision, string SpecPath, string SpecBlob,
    string IntegrationBranch, IReadOnlyList<TaskUnit> Units);
public sealed record TaskUnitResumeRow(string Id, Guid? AttemptId, string? ReportedState, string? ReviewState,
    string IntegrationState, string? OutputBranch, string? OutputCommit, string NextAction, string? InvalidationReason);
public sealed record TaskResumePacket(string TaskId, long? GraphRevision, string? LatestDecisions,
    IReadOnlyList<string> Blockers, IReadOnlyList<TaskUnitResumeRow> Units, int OmittedUnits, string EvidenceRoute, string NextAction);
