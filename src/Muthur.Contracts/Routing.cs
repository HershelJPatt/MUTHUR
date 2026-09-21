namespace Muthur.Contracts;

public sealed record RoutingCandidate(string Harness, string Model, string? Account, string? ReasoningEffort, int CatalogPosition,
    bool Eligible, bool Available, string EligibilityEvidence, int ComparableOutcomes, int? ActualStarts,
    decimal? FalseApprovalRate, decimal? IndependentSuccessRate, decimal? ReworkRate, double? EndToEndSeconds,
    int ReportedSuccesses, int ReportedFailures, IReadOnlyList<string> Evidence, IReadOnlyList<string> MissingData);
public sealed record RoutingHistory(DateTimeOffset From, DateTimeOffset To, long LedgerBoundary,
    IReadOnlyList<WorkerRunDto> Runs, IReadOnlyList<TaskDto> ReadyTasks, int PendingValidation);
public sealed record RoutingReport(int SchemaVersion, string PolicyVersion, DateTimeOffset GeneratedAt, string Task,
    string BaseCommit, string SpecBlob, string WorkKind, DateTimeOffset From, DateTimeOffset To, long LedgerBoundary,
    IReadOnlyList<string> Requirements, IReadOnlyList<RoutingCandidate> Candidates, int? RecommendedCatalogPosition,
    string Rationale, string? OldestReadyTask, int PendingValidation, IReadOnlyList<string> RequiredGates);
public sealed record RoutingSnapshot(long Sequence, DateTimeOffset At, string Actor, RoutingReport Report);
