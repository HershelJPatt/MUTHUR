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

    public int ClaimLeaseMinutes { get; set; } = 30;
    public int RoleLeaseMinutes { get; set; } = 30;
    public int AgentStaleSeconds { get; set; } = 180;
    public int IngestIntervalSeconds { get; set; } = 180;
    /// <summary>When true, outbound text must be reviewed by an agent on a different provider than its author.</summary>
    public bool RequireCrossProviderReview { get; set; }
    /// <summary>Off in tests so sweeps and ingest only run when a test asks for them.</summary>
    public bool BackgroundServices { get; set; } = true;

    /// <summary>
    /// Bot token for <c>discord:</c> ingest, in practice set as the environment variable
    /// <c>Muthur__DiscordBotToken</c>. It is never recorded in the ledger, logged, or returned by an endpoint.
    /// </summary>
    public string? DiscordBotToken { get; set; }

    public string ResolveConnectionString() =>
        ConnectionString ?? $"Data Source={Path.Combine(DataDir, MuthurEnvironment.DatabaseFile)}";
}
