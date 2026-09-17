namespace Muthur.Contracts;

/// <summary>Locations and defaults shared by the CLI and the server.</summary>
public static class MuthurEnvironment
{
    public const string DefaultUrl = "http://127.0.0.1:7420";

    public const string UrlVariable = "MUTHUR_URL";
    public const string HomeVariable = "MUTHUR_HOME";
    public const string TokenVariable = "MUTHUR_TOKEN";
    public const string ServerPathVariable = "MUTHUR_SERVER";

    public const string FounderTokenFile = "founder.token";
    public const string PidFile = "muthur.pid";
    public const string DatabaseFile = "muthur.db";
    public const string LogFile = "muthur.log";
    public const string HarnessFile = "harnesses.json";

    public static string Url =>
        Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? url.TrimEnd('/') : DefaultUrl;

    /// <summary>Data directory: MUTHUR_HOME, else %LOCALAPPDATA%\Muthur (~/.local/share/Muthur elsewhere).</summary>
    public static string Home =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muthur");
}
