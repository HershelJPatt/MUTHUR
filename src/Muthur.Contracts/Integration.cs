namespace Muthur.Contracts;

public sealed record IntegrationCandidateDto(
    Guid Id, Guid AssignmentId, Guid ProjectId, string TaskId, Guid SubjectId,
    string RepositoryPath, string DefaultBranch, string TargetSha, string ImplementationSha,
    string? CandidateSha, string? TreeSha, ValidationChecksDto RequiredChecks, string State,
    Guid? AssignedAgentId, DateTimeOffset LeaseExpires, int Attempt, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, bool AlreadyIncluded,
    DateTimeOffset? PromotionIntentAt, DateTimeOffset? PromotedAt,
    IntegrationEvidenceDto? Evidence, string? EvidenceSha256, string? FailureCode,
    string? FailureMessage, IntegrationFailureRequest? Failure = null,
    int? RunnerProcessId = null, DateTimeOffset? RunnerStartedAt = null,
    string? OwnedWorktreePath = null, string? ArtifactsDirectory = null);

public sealed record IntegrationHistoryDto(IntegrationCandidateDto? Current, IReadOnlyList<IntegrationCandidateDto> History);
public sealed record IntegrationClaimRequest;
public sealed record IntegrationAssignmentDto(IntegrationCandidateDto Candidate, int SessionTimeoutSeconds);

public sealed record IntegrationCandidateRequest(
    Guid AssignmentId, Guid SubjectId, string TargetSha, string ImplementationSha,
    string CandidateSha, string TreeSha, bool AlreadyIncluded = false);

public sealed record IntegrationCheckDto(
    string Name, string Command, int? ExitCode, long DurationMilliseconds,
    string? ArtifactReference, string? ArtifactSha256, bool Skipped = false);

public sealed record IntegrationEvidenceDto(
    Guid AssignmentId, Guid CandidateId, Guid SubjectId, string CheckoutSha,
    IReadOnlyList<IntegrationCheckDto> Checks, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    bool CleanupSucceeded, string? CleanupMessage);

public sealed record IntegrationFailureRequest(
    Guid AssignmentId, Guid SubjectId, string Phase, string Code, string Message,
    string? ArtifactReference = null, string? ArtifactSha256 = null,
    IReadOnlyList<string>? Files = null, IReadOnlyList<string>? LandedSince = null);

public sealed record IntegrationRenewRequest(Guid AssignmentId, Guid SubjectId);

public sealed record IntegrationStartRequest(Guid AssignmentId, Guid SubjectId, int ProcessId,
    DateTimeOffset ProcessStartedAt, string OwnedWorktreePath, string ArtifactsDirectory);
