namespace Muthur.Contracts;

public sealed record OverseerConfig(bool Enabled = false, string Harness = "", string Model = "",
    string? Account = null, string ReasoningEffort = "medium", int SessionMinutes = 20,
    int MaxStartsPerDay = 0, int CooldownMinutes = 10, int ContextChars = 24000, int MemoryChars = 6000);
public sealed record OverseerWait(string Kind, string Target, string Expected, string Reason, string Group = "");
public sealed record OverseerCheckpoint(string Run, string Summary, IReadOnlyList<OverseerWait> Waits, bool Complete = false);
public sealed record OverseerDecision(int Request, string Answer, string Reason, string Evidence);
public sealed record OverseerStatus(OverseerConfig Config, string? Run, string Summary,
    IReadOnlyList<OverseerWait> Waits, DateTimeOffset? LastStarted, int StartsToday, string? LastOutcome);
