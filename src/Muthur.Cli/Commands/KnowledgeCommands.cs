using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class KnowledgeCommands
{
    public static void AddTo(RootCommand root)
    {
        var group = new Command("knowledge", "Review and publish bounded, versioned operating lessons.");
        root.Subcommands.Add(group);
        var list = new Command("list", "List lesson revisions and publication state.");
        list.SetAction(async (p, ct) => Output.Emit(p, await HubClient.For(p).GetAsync("/api/v1/knowledge", ct)));
        group.Subcommands.Add(list);
        var id = new Argument<string>("id");
        var show = new Command("show", "Show immutable revision and publication history.") { id };
        show.SetAction(async (p, ct) => Output.Emit(p, await HubClient.For(p).GetAsync("/api/v1/knowledge/" + Uri.EscapeDataString(p.GetValue(id)!), ct)));
        group.Subcommands.Add(show);
        Write(group, "add", MuthurJsonContext.Default.LessonWriteRequest, false);
        Write(group, "edit", MuthurJsonContext.Default.LessonWriteRequest);
        Write(group, "publish", MuthurJsonContext.Default.LessonPublishRequest);
        Write(group, "retire", MuthurJsonContext.Default.LessonRetireRequest);
    }

    private static void Write<T>(Command group, string name, JsonTypeInfo<T> json, bool existing = true) where T : class
    {
        var file = new Option<string>("--file") { Required = true };
        var id = new Argument<string>("id");
        var command = new Command(name, "Submit the JSON definition or revision-specific action.") { file };
        if (existing) command.Arguments.Add(id);
        command.SetAction(async (p, ct) =>
        {
            try
            {
                var body = JsonSerializer.Deserialize(await File.ReadAllTextAsync(p.GetValue(file)!, ct), json) ?? throw new JsonException("A JSON request is required.");
                return Output.Emit(p, await HubClient.For(p).PostAsync("/api/v1/knowledge" + (existing ? "/" + Uri.EscapeDataString(p.GetValue(id)!) + "/" + name : ""), body, json, ct));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { return Output.Error("knowledge_input", ex.Message, 2); }
        });
        group.Subcommands.Add(command);
    }
}
