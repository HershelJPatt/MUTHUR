using System.Text.Json.Serialization;

namespace Muthur.Contracts;

/// <summary>ok: nothing to do. warn: worth knowing, nothing is broken. fail: the hub cannot do this job.</summary>
public enum CheckStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("warn")] Warn,
    [JsonStringEnumMemberName("fail")] Fail,
}

/// <param name="Category">"secret", "logging", "ingest", "outbound", "project", "repo", "role", "harness" or "agent".</param>
/// <param name="Subject">What was checked, safe to show: a source, a target key, a project key, a role key.</param>
/// <param name="Detail">One sentence: what is true, and what to do about it.</param>
public sealed record CheckDto(
    string Category,
    string Subject,
    CheckStatus Status,
    string Detail,
    DateTimeOffset? LastSuccess = null);

public sealed record DoctorDto(
    DateTimeOffset At,
    bool Probed,
    int Ok,
    int Warn,
    int Fail,
    IReadOnlyList<CheckDto> Checks);
