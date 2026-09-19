using System.Text.Json;

namespace Muthur.Contracts;

public sealed record AddTaskRequest(string Title, string? Project = null, string? Body = null, int Priority = 0, string? Parent = null);

public sealed record ClaimTaskRequest(int? LeaseMinutes = null);

public sealed record ReleaseTaskRequest(string? Reason = null);

public sealed record SetSpecRequest(string Path, string? Branch = null);

public sealed record SetPriorityRequest(int Priority);

public sealed record CancelTaskRequest(string? Reason = null);

/// <param name="Reason">Why a human is needed. Null clears the flag.</param>
public sealed record AttendedRequest(string? Reason);

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
    DateTimeOffset? HoldExpires = null);

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
    DateTimeOffset? ClaimExpires);

/// <param name="Waiting">Tasks in 'validating' with a pending verdict for this role.</param>
/// <param name="Claimed">How many of those a validator has taken.</param>
public sealed record ValidationQueueDto(
    string Role,
    int Capacity,
    int Holders,
    int Waiting,
    int Claimed,
    DateTimeOffset? OldestWaitingSince);

public sealed record TaskDetailDto(TaskDto Task, IReadOnlyList<EventDto> Events);

public sealed record EventDto(
    long Seq,
    DateTimeOffset At,
    string Actor,
    string? ActorModel,
    string Type,
    string? TaskId,
    JsonElement Payload);
