namespace Muthur.Contracts;

public sealed record ObjectiveMeasurement(string Metric, decimal? Value, string Quality, string Unit, int SampleCount,
    DateTimeOffset From, DateTimeOffset To, IReadOnlyList<string> Cohort, IReadOnlyList<string> Revisions, string Evidence, string MissingData);
public sealed record ObjectiveApproval(string Task, long Event, int? Request = null);
public sealed record ObjectiveDefinition(ObjectiveApproval Approval, string Outcome, string Scope, ObjectiveMeasurement Baseline,
    string SuccessCondition, DateTimeOffset WindowStart, DateTimeOffset WindowEnd, string Metric, decimal Target,
    string Comparison, int MinimumSample, string Reason);
public sealed record ObjectiveRevision(int Number, int? PreviousRevision, string Actor, DateTimeOffset At,
    ObjectiveDefinition Definition, string ApprovalText);
public sealed record ObjectiveLink(string Task, string Role, string Note);
public sealed record ObjectiveEvidence(string Key, int Revision, string Kind, ObjectiveMeasurement Measurement,
    string Description, string? Task, long? Event, string Actor, DateTimeOffset At);
public sealed record ObjectiveEffort(string Key, decimal Minutes, string Quality, DateTimeOffset From, DateTimeOffset To,
    string Source, string Note, string Actor, DateTimeOffset At);
public sealed record ObjectiveAcceptance(int Revision, string ObservationKey, string Actor, DateTimeOffset At, string Reason);
public sealed record ObjectiveDto(string Id, string Project, string Owner, string Title, int Revision, string State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ObjectiveRevision> Revisions,
    IReadOnlyList<ObjectiveLink> Links, IReadOnlyList<ObjectiveEvidence> Evidence, IReadOnlyList<ObjectiveEffort> Effort,
    IReadOnlyList<ObjectiveAcceptance> Acceptances, string? SupersededBy = null, string? ClosureReason = null, int FormatVersion = 1);
public sealed record AddObjectiveRequest(string Project, string Title, ObjectiveDefinition Definition, string? Owner = null);
public sealed record AmendObjectiveRequest(int ExpectedRevision, ObjectiveDefinition Definition, string? Owner = null);
public sealed record LinkObjectiveRequest(int ExpectedRevision, string Task, string Role, string Note);
public sealed record ObserveObjectiveRequest(int ExpectedRevision, string Key, string Kind, ObjectiveMeasurement Measurement,
    string Description, string? Task = null, long? Event = null);
public sealed record EffortObjectiveRequest(int ExpectedRevision, string Key, decimal Minutes, string Quality,
    DateTimeOffset From, DateTimeOffset To, string Source, string Note);
public sealed record AcceptObjectiveRequest(int ExpectedRevision, string ObservationKey, string Reason);
public sealed record CloseObjectiveRequest(int ExpectedRevision, string State, string Reason, string? SupersededBy = null);
public sealed record ObjectiveTaskView(string Id, string Title, string State, string Role, bool DeliveredCapability);
public sealed record ObjectiveBlocker(string Task, string Dependency, string Reason);
public sealed record ObjectiveDecision(int Id, string Task, string Question, string? Answer);
public sealed record ObjectiveView(ObjectiveDto Objective, IReadOnlyList<ObjectiveTaskView> Tasks,
    IReadOnlyList<ObjectiveBlocker> Blockers, IReadOnlyList<ObjectiveDecision> Decisions, string ObservationStatus,
    decimal MeasuredFounderMinutes, decimal EstimatedFounderMinutes, decimal? OutcomesPerMeasuredFounderHour,
    decimal? OutcomesPerEstimatedFounderHour, bool RegressionAfterAcceptance, IReadOnlyList<string> MissingData,
    ObjectiveResources Resources);
public sealed record ObjectiveRun(long Sequence, WorkerRunDto Run);
public sealed record ObjectiveDelivery(string Task, double Seconds, string? Revision);
public sealed record ObjectiveResources(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<string> Cohort,
    IReadOnlyList<ObjectiveRun> Runs, int StaffingAttempts, int? ActualStarts, int WorkerReports,
    double RecordedWorkerSeconds, double WaitingTaskSeconds, int RepeatedDependencyParkings, int FounderInterventions,
    IReadOnlyList<ObjectiveDelivery> Deliveries, double? MedianDeliverySeconds, double? P90DeliverySeconds,
    IReadOnlyList<string> MissingData);
