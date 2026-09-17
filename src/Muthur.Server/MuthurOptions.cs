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

    public string ResolveConnectionString() =>
        ConnectionString ?? $"Data Source={Path.Combine(DataDir, MuthurEnvironment.DatabaseFile)}";
}
