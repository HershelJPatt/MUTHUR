using System.Text.Json;

namespace Muthur.Contracts;

public sealed record AddTaskRequest(string Title, string? Project = null, string? Body = null, int Priority = 0, string? Parent = null);

public sealed record ClaimTaskRequest(int? LeaseMinutes = null);

public sealed record ReleaseTaskRequest(string? Reason = null);
public sealed record DependenciesRequest(IReadOnlyList<string> Tasks, string? Reason = null);

public sealed record SetSpecRequest(string Path, string? Branch = null);

public sealed record RevalidateRequest(string Reason);

/// <summary>The immutable implementation and policy reviewed in one validation round.</summary>
public sealed record ValidationSubjectDto(
    Guid Id, string TaskId, Guid ProjectId, string RepositoryPath, string ImplementationSha,
    string SpecPath, string SpecSha256, IReadOnlyList<string> RequiredValidators,
    ValidationChecksDto RequiredChecks, ValidationEnvironmentDto InvalidatingEnvironment,
    ValidationMetadataDto DescriptiveMetadata, DateTimeOffset CreatedAt);

/// <summary>
/// The checks a round is judged by, pinned at the implementation commit: the project's build and test commands,
/// the commands the spec wrote under its Verification heading, and whether a model has to look as well.
/// <c>RequiresJudgment</c> defaults to true so a subject frozen before specs carried a Verification section keeps
/// getting a validator session.
/// </summary>
/// <param name="ChecksOnlyValidation">
/// The project's <c>checksOnlyValidation</c> switch as committed in <c>muthur.project.json</c> at the implementation
/// commit. It lives in the committed file rather than on the project row so that it is versioned with the build and
/// test commands it hands the verdict to, and a round is judged by the policy its own commit declared.
/// </param>
public sealed record ValidationChecksDto(string? Build, string? Test, IReadOnlyList<string>? Commands = null,
    bool RequiresJudgment = true, bool ChecksOnlyValidation = false)
{
    // A list member would make two copies of the same pinned policy unequal; the commands are compared as values.
    public bool Equals(ValidationChecksDto? other) => other is not null && Build == other.Build && Test == other.Test
        && RequiresJudgment == other.RequiresJudgment && ChecksOnlyValidation == other.ChecksOnlyValidation
        && (Commands ?? []).SequenceEqual(other.Commands ?? [], StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Build, Test, RequiresJudgment, ChecksOnlyValidation, (Commands ?? []).Count);
}
public sealed record ValidatorBriefDigestDto(string Role, string Sha256);
public sealed record ValidationEnvironmentDto(string ProjectKey, string DefaultBranch, string LandingMode,
    IReadOnlyList<ValidatorBriefDigestDto> Briefs);
public sealed record ValidationMetadataDto(string OperatingSystem, string Architecture, string Runtime, string? HostRevision = null, string? DesignSha256 = null);

public sealed record SetPriorityRequest(int Priority);

public sealed record CancelTaskRequest(string? Reason = null);

/// <param name="Reason">Why a human is needed. Null clears the flag.</param>
public sealed record AttendedRequest(string? Reason);

/// <summary>
/// The owner's working notes on a task: what it learned becoming the expert, what it decided and why, what is
/// left. Written before a session stops on a question or a limit, read by the session that resumes the task, so
/// the resumption starts from understanding rather than from the codebase. At most <see cref="MaxChars"/>.
/// </summary>
public sealed record TaskNotesRequest(string Notes)
{
    public const int MaxChars = 8000;
}

public sealed record TaskDto(
    string Id,
    string Project,
    string Title,
    string Body,
    TaskState State,
    int Priority,
    string? Owner,
    DateTimeOffset? ClaimExpires,
    string? SpecPath,
    string? Branch,
    string? PrUrl,
    string? Parent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DoneAt,
    IReadOnlyList<ValidationDto> Validations,
    string? AttendedReason,
    string? HoldReason = null,
    string? HoldBy = null,
    DateTimeOffset? HoldExpires = null,
    IReadOnlyList<string>? DependsOn = null,
    string? DependencyReason = null,
    ValidationSubjectDto? CurrentSubject = null,
    string ProvenanceStatus = "unknown",
    IntegrationCandidateDto? CurrentIntegrationCandidate = null);

/// <summary>A reason that is null or blank is the clear, the same convention <c>attended</c> uses.</summary>
public sealed record HoldRequest(string? Reason);

public sealed record ValidationDto(
    string Validator,
    string Verdict,
    string? Agent,
    string? Evidence,
    DateTimeOffset? At,
    DateTimeOffset WaitingSince,
    string? ClaimedBy,
    DateTimeOffset? ClaimExpires,
    Guid? SubjectId = null,
    string EvidenceStatus = "unknown");

/// <param name="Waiting">Tasks in 'validating' with a pending verdict for this role.</param>
/// <param name="Claimed">How many of those a validator has taken.</param>
public sealed record ValidationQueueDto(
    string Role,
    int Capacity,
    int Holders,
    int Waiting,
    int Claimed,
    DateTimeOffset? OldestWaitingSince);

/// <param name="WorkKindSuggested">What the utility tier guessed the work is at intake; advisory, the spec's work-kind line decides.</param>
public sealed record TaskDetailDto(TaskDto Task, IReadOnlyList<EventDto> Events, IReadOnlyList<TaskIncidentDto>? Incidents = null, string? Notes = null,
    string? WorkKindSuggested = null);

/// <summary>One of the four work-kinds a spec may declare, suggested for a task before it has a spec.</summary>
public sealed record WorkKindRequest(string WorkKind)
{
    public static readonly string[] Kinds = ["mechanical", "complex-debugging", "ui-interaction", "general"];
}

public sealed record EventDto(
    long Seq,
    DateTimeOffset At,
    string Actor,
    string? ActorModel,
    string Type,
    string? TaskId,
    JsonElement Payload);
