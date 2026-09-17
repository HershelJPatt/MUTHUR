using System.Text.Json;

namespace Muthur.Contracts;

public sealed record AddTaskRequest(string Title, string? Project = null, string? Body = null, int Priority = 0, string? Parent = null);

public sealed record ClaimTaskRequest(int? LeaseMinutes = null);

public sealed record ReleaseTaskRequest(string? Reason = null);

public sealed record SetSpecRequest(string Path);

public sealed record SetPriorityRequest(int Priority);

public sealed record CancelTaskRequest(string? Reason = null);

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
    IReadOnlyList<ValidationDto> Validations);

public sealed record ValidationDto(string Validator, string Verdict, string? Agent, string? Evidence, DateTimeOffset? At);

public sealed record TaskDetailDto(TaskDto Task, IReadOnlyList<EventDto> Events);

public sealed record EventDto(
    long Seq,
    DateTimeOffset At,
    string Actor,
    string? ActorModel,
    string Type,
    string? TaskId,
    JsonElement Payload);
