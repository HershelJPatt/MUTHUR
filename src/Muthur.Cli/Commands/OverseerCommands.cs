using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class OverseerCommands
{
    public static void AddTo(RootCommand root)
    {
        var command = new Command("overseer", "Organization oversight, model configuration and bounded durable memory.");
        root.Subcommands.Add(command);
        var status = new Command("status", "Show configuration, checkpoint, waits and daily usage.");
        status.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Api + "/overseer", ct)));
        command.Subcommands.Add(status);
        var configFile = new Option<string>("--file") { Required = true, Description = "JSON OverseerConfig: enabled, harness, model, account, reasoningEffort and budget limits. Replaces the complete configuration; requires --founder." };
        var config = new Command("configure", "Configure provider/model/effort and budgets; applies to the next fresh session.") { configFile };
        config.SetAction(async (parse, ct) =>
        {
            var value = JsonSerializer.Deserialize(await File.ReadAllTextAsync(parse.GetValue(configFile)!, ct), MuthurJsonContext.Default.OverseerConfig)
                ?? throw new InvalidOperationException("Configuration is required.");
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Api + "/overseer/config", value, MuthurJsonContext.Default.OverseerConfig, ct));
        });
        command.Subcommands.Add(config);
        var checkpointFile = new Option<string>("--file") { Required = true };
        var checkpoint = new Command("checkpoint", "Save bounded memory and durable wake conditions before exiting.") { checkpointFile };
        checkpoint.SetAction(async (parse, ct) =>
        {
            var value = JsonSerializer.Deserialize(await File.ReadAllTextAsync(parse.GetValue(checkpointFile)!, ct), MuthurJsonContext.Default.OverseerCheckpoint)
                ?? throw new InvalidOperationException("Checkpoint is required.");
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Api + "/overseer/checkpoint", value, MuthurJsonContext.Default.OverseerCheckpoint, ct));
        });
        command.Subcommands.Add(checkpoint);
        var id = new Argument<int>("id");
        var answer = new Argument<string>("answer");
        var reason = new Option<string>("--reason") { Required = true };
        var evidence = new Option<string>("--evidence") { Required = true };
        var decide = new Command("decide", "Answer an explicitly technical request as the active overseer.") { id, answer, reason, evidence };
        decide.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Api + "/overseer/decide",
            new OverseerDecision(parse.GetValue(id), parse.GetValue(answer)!, parse.GetValue(reason)!, parse.GetValue(evidence)!), MuthurJsonContext.Default.OverseerDecision, ct)));
        command.Subcommands.Add(decide);
    }
}
