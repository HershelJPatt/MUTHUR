using System.Text.Json.Serialization;

namespace Muthur.Contracts;

public sealed record AddIncidentRequest(string Title, string Signature, string Path, string Configuration, string Recovery, string? Project = null);
public sealed record UpdateIncidentRequest(string Diagnosis, string? Workaround = null, string? Authorization = null);
public sealed record TransitionIncidentRequest(string State, string Evidence);
public sealed record ObserveIncidentRequest(string Task, string Evidence, string Signature, string Path, string Configuration, string? Run = null);
public sealed record UnlinkIncidentRequest(int Observation, string Reason);
public sealed record SuppressIncidentRequest(string Task, string Assignment, int Observation, string Reason);
public sealed record RecoverIncidentRequest(string Kind, string Evidence, string? Configuration = null);

public sealed record IncidentDto(string Id, string Project, string Title, string State, string Signature,
    string ExecutionPath, string Configuration, int ConditionVersion, string Diagnosis, string Workaround,
    string AuthorizationReference, string RecoveryCondition, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record IncidentObservationDto(int Id, string IncidentId, string TaskId, string? RunId, string Evidence,
    string Signature, string ExecutionPath, string Configuration, int ConditionVersion, DateTimeOffset CreatedAt,
    string Actor, bool Active, string? UnlinkReason);
public sealed record IncidentSuppressionDto(string IncidentId, string TaskId, string Assignment, int ConditionVersion,
    int EvidenceObservationId, string Reason, DateTimeOffset CreatedAt, bool Active, DateTimeOffset? ReleasedAt,
    string? ReleaseReason, bool Effective, string EffectiveReason, string RecoveryCondition);
public sealed record IncidentDetailDto(IncidentDto Incident, IReadOnlyList<IncidentObservationDto> Observations,
    IReadOnlyList<IncidentSuppressionDto> Suppressions, IReadOnlyList<EventDto> Events);
public sealed record IncidentMatchesDto(bool Advisory, IReadOnlyList<IncidentDto> Matches);
public sealed record TaskIncidentDto(IncidentDto Incident, IReadOnlyList<IncidentObservationDto> Observations,
    IReadOnlyList<IncidentSuppressionDto> Suppressions);
public sealed record IncidentStaffingElapsedDto(string TaskId, long RecoverySeq,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? StaffingSeq,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? ElapsedSeconds);
public sealed record IncidentMetricsDto(string IncidentId, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    IReadOnlyList<string> LinkedTaskCohort, int ObservationCount, int DistinctTaskCount, int RecordedDiagnosisUpdates,
    int UnlinkCorrections, int SuppressionCount, int ReleaseCount, int GroupingCorrectionReleaseCount,
    int ConductorStaffingAttempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ActualProcessStarts, int WorkerRuns, int SuccessfulTaskOutcomes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DiagnosisSessions, IReadOnlyList<IncidentStaffingElapsedDto> RecoveryToFirstStaffing,
    IReadOnlyList<long> SourceEventSequenceIds, string ImplementationRevision, IReadOnlyList<string> MissingData);
