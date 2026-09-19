using Muthur.Contracts;

namespace Muthur.Server;

/// <summary>Bound from the "Muthur" configuration section. Everything deployment-specific lives here.</summary>
public sealed class MuthurOptions
{
    public string Url { get; set; } = MuthurEnvironment.Url;
    public string DataDir { get; set; } = MuthurEnvironment.Home;
    public string DbProvider { get; set; } = "sqlite";
    /// <summary>Optional override; by default SQLite lives at {DataDir}/muthur.db.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Where the agent kit is. Empty means: $MUTHUR_KIT, else kit/ beside the hub, else kit/ beside its parent —
    /// which is how install.ps1 lays an install out, with the server in server/ and the kit next to it.
    /// </summary>
    public string? KitDir { get; set; }

    public int ClaimLeaseMinutes { get; set; } = 30;
    public int RoleLeaseMinutes { get; set; } = 30;
    /// <summary>
    /// How long a hold on a task counts for. Two hours spans several thirty-minute orchestrator rounds, so a
    /// hold placed in one iteration is still there for the next, and clears itself inside the session that
    /// placed it — a permanent note nobody cleared is text the next lander learns to skip.
    /// </summary>
    public int HoldMinutes { get; set; } = 120;
    public int AgentStaleSeconds { get; set; } = 180;
    public int IngestIntervalSeconds { get; set; } = 180;
    /// <summary>When true, outbound text must be reviewed by an agent on a different provider than its author.</summary>
    public bool RequireCrossProviderReview { get; set; }
    /// <summary>Off in tests so sweeps and ingest only run when a test asks for them.</summary>
    public bool BackgroundServices { get; set; } = true;

    /// <summary>
    /// Whether the conductor may start validator sessions. Off by default: upgrading a hub must never begin
    /// spending the founder's subscription on its own.
    /// </summary>
    public bool ConductorEnabled { get; set; }
    /// <summary>How many validator sessions the conductor may have running at once.</summary>
    public int ConductorMaxSessions { get; set; } = 2;
    /// <summary>A validator session that has not finished by then is killed; the role's lease then lapses on its own.</summary>
    public int ConductorSessionMinutes { get; set; } = 45;
    /// <summary>Failed verdicts on one task before the conductor stops restaffing it and asks the founder.</summary>
    public int ConductorMaxAttempts { get; set; } = 3;
    public int ConductorIntervalSeconds { get; set; } = 60;
    /// <summary>The interval the conductor actually runs at: a floor, so a small number cannot turn it into a spin.</summary>
    public int EffectiveConductorIntervalSeconds => Math.Max(MinimumConductorIntervalSeconds, ConductorIntervalSeconds);
    public const int MinimumConductorIntervalSeconds = 15;
    /// <summary>After a pair gives up starting, how long before one probe is let through. Its cause is usually fixed from outside the hub.</summary>
    public int ConductorStallProbeMinutes { get; set; } = 30;

    /// <summary>
    /// The ceiling on one `doctor` run, whatever its checks do. Under the CLI's 180s client timeout, so a
    /// bounded hub still answers before the caller gives up, and above two of T-14's 60s probes, so an
    /// ordinary two-source hub that is merely slow is not reported as one that did not answer.
    /// </summary>
    public int DoctorBudgetSeconds { get; set; } = 120;

    /// <summary>
    /// Bot token for <c>discord:</c> ingest, in practice set as the environment variable
    /// <c>Muthur__DiscordBotToken</c>. It is never recorded in the ledger, logged, or returned by an endpoint.
    /// </summary>
    public string? DiscordBotToken { get; set; }

    public string ResolveConnectionString() =>
        ConnectionString ?? $"Data Source={Path.Combine(DataDir, MuthurEnvironment.DatabaseFile)}";
}
