using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class ObjectiveCommands
{
    public static void AddTo(RootCommand root)
    {
        var group = new Command("objective", "Record approved outcomes and their attributed evidence. Output is JSON.");
        root.Subcommands.Add(group);
        var list = new Command("list", "List recorded objectives.");
        list.SetAction(async (p, ct) => Output.Emit(p, await HubClient.For(p).GetAsync("/api/v1/objectives", ct)));
        group.Subcommands.Add(list);
        var id = new Argument<string>("id");
        var show = new Command("show", "Show outcome evidence, history and blockers.") { id };
        show.SetAction(async (p, ct) => Output.Emit(p, await HubClient.For(p).GetAsync("/api/v1/objectives/" + Uri.EscapeDataString(p.GetValue(id)!), ct)));
        group.Subcommands.Add(show);
        Write(group, "add", MuthurJsonContext.Default.AddObjectiveRequest, false);
        Write(group, "amend", MuthurJsonContext.Default.AmendObjectiveRequest);
        Write(group, "link", MuthurJsonContext.Default.LinkObjectiveRequest);
        Write(group, "observe", MuthurJsonContext.Default.ObserveObjectiveRequest);
        Write(group, "effort", MuthurJsonContext.Default.EffortObjectiveRequest);
        Write(group, "accept", MuthurJsonContext.Default.AcceptObjectiveRequest);
        Write(group, "close", MuthurJsonContext.Default.CloseObjectiveRequest);
    }

    private static void Write<T>(Command group, string name, JsonTypeInfo<T> json, bool existing = true) where T : class
    {
        var file = new Option<string>("--file") { Required = true, Description = "UTF-8 JSON request file." };
        var id = new Argument<string>("id");
        var command = new Command(name, "Submit an attributed objective change from a JSON file.") { file };
        if (existing) command.Arguments.Add(id);
        command.SetAction(async (p, ct) =>
        {
            var value = JsonSerializer.Deserialize(await File.ReadAllTextAsync(p.GetValue(file)!, ct), json)
                ?? throw new JsonException("A request object is required.");
            var path = "/api/v1/objectives" + (existing ? "/" + Uri.EscapeDataString(p.GetValue(id)!) + "/" + name : "");
            Output.Emit(p, await HubClient.For(p).PostAsync(path, value, json, ct));
        });
        group.Subcommands.Add(command);
    }
}
