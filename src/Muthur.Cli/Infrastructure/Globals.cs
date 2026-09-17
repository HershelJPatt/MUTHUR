using System.CommandLine;
using Muthur.Contracts;

namespace Muthur.Cli.Infrastructure;

/// <summary>Options accepted by every command.</summary>
public static class Globals
{
    public static readonly Option<bool> Pretty = new("--pretty") { Description = "Indent JSON output for humans.", Recursive = true };
    public static readonly Option<string?> Token = new("--token") { Description = $"Bearer token (default: ${MuthurEnvironment.TokenVariable}).", Recursive = true };
    public static readonly Option<string?> As = new("--as-agent") { Description = $"Act as a registered agent by name (default: ${AgentVariable}).", Recursive = true };
    public static readonly Option<bool> Founder = new("--founder") { Description = "Act as the founder (human operator).", Recursive = true };

    public const string AgentVariable = "MUTHUR_AGENT";

    public static void AddTo(RootCommand root)
    {
        root.Options.Add(Pretty);
        root.Options.Add(Token);
        root.Options.Add(As);
        root.Options.Add(Founder);
    }

    public static string AgentTokenPath(string agentName) =>
        Path.Combine(MuthurEnvironment.Home, "agents", agentName + ".token");

    /// <summary>--token, then $MUTHUR_TOKEN, then --founder, then the token file of --as-agent / $MUTHUR_AGENT.</summary>
    public static string? ResolveToken(ParseResult parse)
    {
        if (parse.GetValue(Token) is { Length: > 0 } explicitToken) return explicitToken;
        if (Environment.GetEnvironmentVariable(MuthurEnvironment.TokenVariable) is { Length: > 0 } envToken) return envToken;
        if (parse.GetValue(Founder)) return ReadFounderToken();

        var agent = parse.GetValue(As) is { Length: > 0 } a ? a : Environment.GetEnvironmentVariable(AgentVariable);
        if (agent is { Length: > 0 } && File.Exists(AgentTokenPath(agent)))
            return File.ReadAllText(AgentTokenPath(agent)).Trim();
        return null;
    }

    public static string? ReadFounderToken()
    {
        var path = Path.Combine(MuthurEnvironment.Home, MuthurEnvironment.FounderTokenFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
