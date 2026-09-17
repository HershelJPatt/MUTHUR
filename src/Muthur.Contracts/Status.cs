namespace Muthur.Contracts;

public sealed record StatusResponse(
    string Name,
    string Version,
    string InstanceId,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset Now,
    string DataDirectory,
    string DbProvider,
    /// <summary>Directory the server binary runs from; lets a CLI tell "its" hub from another installation.</summary>
    string ServerDirectory);
