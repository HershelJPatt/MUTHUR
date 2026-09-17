namespace Muthur.Contracts;

/// <summary><see cref="To"/>: an agent name, "role:&lt;key&gt;", or "founder". A bare name that is not an agent is tried as a role.</summary>
public sealed record SendMessageRequest(string To, string Body, bool Blocking = false, string? Task = null);

public sealed record MessageDto(
    long Id,
    string From,
    string To,
    string Body,
    bool Blocking,
    string? Task,
    DateTimeOffset At,
    DateTimeOffset? ReadAt,
    string? ReadBy);

public sealed record InboxDto(IReadOnlyList<MessageDto> Messages, bool TimedOut);

public sealed record AskRequest(string Question, string? Task = null, IReadOnlyList<string>? Options = null);

public sealed record AnswerRequest(string Answer);

public sealed record FounderRequestDto(
    int Id,
    string? Task,
    string? TaskTitle,
    string Agent,
    string Question,
    IReadOnlyList<string> Options,
    string Status,
    string? Answer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AnsweredAt);
