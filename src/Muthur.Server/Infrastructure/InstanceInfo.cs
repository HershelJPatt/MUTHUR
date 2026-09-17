namespace Muthur.Server.Infrastructure;

/// <summary>Facts about this running hub process, fixed at startup.</summary>
public sealed class InstanceInfo
{
    public string InstanceId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string FounderToken { get; set; } = "";
}
