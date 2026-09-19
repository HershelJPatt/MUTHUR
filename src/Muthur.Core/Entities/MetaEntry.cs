namespace Muthur.Core.Entities;

/// <summary>Instance-level key/value facts (instance id, creation time).</summary>
public sealed class MetaEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }

    public const string InstanceId = "instance_id";
    public const string CreatedAt = "created_at";
    /// <summary>Whether the conductor staffs validation. In the database because the hub is off for hours at a time.</summary>
    public const string ConductorEnabled = "conductor_enabled";
    /// <summary>Sessions the conductor may run at once, as digits ("4"). Absent means Muthur:ConductorMaxSessions.</summary>
    public const string ConductorSessions = "conductor_sessions";
    /// <summary>A lower ceiling for the hours nobody is watching, as "22:00-07:00@1" in local time. Absent means none.</summary>
    public const string ConductorUnattended = "conductor_unattended";
    /// <summary>
    /// Whether the conductor also starts orchestrator sessions from the backlog. Absent means no, so a hub that
    /// upgrades into this build begins spending nothing it was not spending yesterday.
    /// </summary>
    public const string ConductorOrchestrators = "conductor_orchestrators";
}
