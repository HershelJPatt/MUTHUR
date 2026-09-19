using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class AgentCommands
{
    public static void AddTo(RootCommand root)
    {
        var agent = new Command("agent", "Register and report on agents (orchestrator-level sessions).");
        root.Subcommands.Add(agent);

        var name = new Option<string>("--name") { Description = "Agent name, e.g. the terminal pane it lives in (top-left).", Required = true };
        var harness = new Option<string>("--harness") { Description = "Agent harness: claude, codex, ...", Required = true };
        var model = new Option<string>("--model") { Description = "Model the harness runs.", Required = true };
        var tier = new Option<string?>("--tier") { Description = "mastermind | implementer | utility" };
        var account = new Option<string?>("--account") { Description = "Label of the subscription/API account in use." };
        var register = new Command("register", "Register this session as an agent. Stores its token for later calls with MUTHUR_AGENT=<name>.") { name, harness, model, tier, account };
        register.SetAction(async (parse, ct) =>
        {
            var agentName = parse.GetValue(name)!.Trim().ToLowerInvariant();
            // Re-registering an existing name requires being that agent (or the founder), so present its stored token.
            var token = Globals.ResolveToken(parse);
            if (token is null && File.Exists(Globals.AgentTokenPath(agentName)))
                token = File.ReadAllText(Globals.AgentTokenPath(agentName)).Trim();

            var result = await new HubClient(token).PostAsync(Routes.AgentRegister,
                new RegisterAgentRequest(agentName, parse.GetValue(harness)!, parse.GetValue(model)!, parse.GetValue(tier), parse.GetValue(account)),
                MuthurJsonContext.Default.RegisterAgentRequest, ct);
            if (!result.IsSuccess) return Output.Emit(parse, result);

            var registered = JsonSerializer.Deserialize(result.Body, MuthurJsonContext.Default.RegisterAgentResponse)!;
            var path = Globals.AgentTokenPath(registered.Agent.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, registered.Token);
            // The token itself is deliberately not echoed into the agent's transcript.
            return Output.Emit(parse, new ApiResult(200, JsonSerializer.Serialize(registered.Agent, MuthurJsonContext.Default.AgentDto)));
        });
        agent.Subcommands.Add(register);

        var summary = new Option<string?>("--summary") { Description = "One line on what you are doing right now; shown on the dashboard." };
        var heartbeat = new Command("heartbeat", "Signal liveness and renew every task claim and role you hold.") { summary };
        heartbeat.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.AgentHeartbeat, new HeartbeatRequest(parse.GetValue(summary)), MuthurJsonContext.Default.HeartbeatRequest, ct)));
        agent.Subcommands.Add(heartbeat);

        var minutes = new Option<int?>("--minutes") { Description = "Rate-limited for this many minutes from now." };
        var until = new Option<DateTimeOffset?>("--until") { Description = "Rate-limited until this time (ISO 8601)." };
        var clear = new Option<bool>("--clear") { Description = "The limit is over." };
        var limited = new Command("limited", "Report that your account hit its usage limit, so work is routed elsewhere.") { minutes, until, clear };
        limited.SetAction(async (parse, ct) =>
        {
            DateTimeOffset? value = parse.GetValue(clear) ? null
                : parse.GetValue(until) ?? (parse.GetValue(minutes) is { } m ? DateTimeOffset.UtcNow.AddMinutes(m) : null);
            if (value is null && !parse.GetValue(clear))
                return Output.Error("until_required", "Pass --minutes, --until or --clear.", ExitCodes.RuleViolation);
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.AgentLimited, new LimitedRequest(value), MuthurJsonContext.Default.LimitedRequest, ct));
        });
        agent.Subcommands.Add(limited);

        var whoami = new Command("whoami", "Show the agent this session is acting as.");
        whoami.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.AgentMe, ct)));
        agent.Subcommands.Add(whoami);

        var listAll = new Option<bool>("--all") { Description = "Include the sessions the conductor staffed." };
        var list = new Command("list", "List standing agents with status, roles and open task counts.") { listAll };
        list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(
            Routes.Agents + AgentListQuery(parse.GetValue(listAll)), ct)));
        agent.Subcommands.Add(list);
    }

    /// <summary>The query behind `agent list`. One optional filter, so the leading '?' belongs to it.</summary>
    internal static string AgentListQuery(bool all) => all ? "?all=true" : "";
}
