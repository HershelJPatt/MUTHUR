namespace Muthur.Contracts;

public sealed record StatusResponse(
    string Name,
    string Version,
    string InstanceId,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset Now,
    string DataDirectory,
    string DbProvider);
