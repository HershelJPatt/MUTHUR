namespace Muthur.Contracts;

public sealed record WorkerAdmissionRequest(string Task, string Tier, string Harness, string Model, string Account,
    string RunId, string InputHash);
public sealed record WorkerAdmissionDto(string ReservationId, string Task, string RunId, bool MayExecute);
public sealed record WorkerReleaseRequest(string ReservationId, bool CleanupConfirmed);
public sealed record WorkerReservationDto(string ReservationId, WorkerAdmissionRequest Request, DateTimeOffset AdmittedAt);
public sealed record WorkerAttemptReport(string Harness, string Model, string? Account, string? RunId,
    string? ReservationId, bool Started, string? FailureKind, string? Cleanup);
