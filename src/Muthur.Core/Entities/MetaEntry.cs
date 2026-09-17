namespace Muthur.Core.Entities;

/// <summary>Instance-level key/value facts (instance id, creation time).</summary>
public sealed class MetaEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }

    public const string InstanceId = "instance_id";
    public const string CreatedAt = "created_at";
}
