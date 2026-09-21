using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class TaskUnitCommands
{
    public static void AddTo(Command task)
    {
        var unitsId = new Argument<string>("id");
        var define = new Option<string?>("--define") { Description = "Define the graph from a JSON file with expectedRevision." };
        var units = new Command("units", "Read the complete work-unit graph, or define it from JSON.") { unitsId, define };
        units.SetAction(async (parse, ct) =>
        {
            var route = Routes.TaskUnits(parse.GetValue(unitsId)!);
            if (parse.GetValue(define) is not { } file)
                return Output.Emit(parse, await HubClient.For(parse).GetAsync(route, ct));
            try
            {
                var request = await ReadFile(file, MuthurJsonContext.Default.DefineTaskUnitsRequest, ct);
                return Output.Emit(parse, await HubClient.For(parse).PostAsync(route + "/define", request, MuthurJsonContext.Default.DefineTaskUnitsRequest, ct));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            { return Output.Error("unit_json_file", ex.Message, ExitCodes.RuleViolation); }
        });
        task.Subcommands.Add(units);

        var checkpointId = new Argument<string>("id");
        var checkpointFile = new Option<string>("--file") { Required = true, Description = "Transition JSON including expectedRevision and attemptId." };
        var checkpoint = new Command("checkpoint", "Record one evidence-linked unit transition.") { checkpointId, checkpointFile };
        checkpoint.SetAction(async (parse, ct) =>
        {
            try
            {
                var request = await ReadFile(parse.GetValue(checkpointFile)!, MuthurJsonContext.Default.TaskUnitCheckpointRequest, ct);
                return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.TaskUnits(parse.GetValue(checkpointId)!) + "/checkpoint",
                    request, MuthurJsonContext.Default.TaskUnitCheckpointRequest, ct));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            { return Output.Error("unit_json_file", ex.Message, ExitCodes.RuleViolation); }
        });
        task.Subcommands.Add(checkpoint);

        var resumeId = new Argument<string>("id");
        var resume = new Command("resume", "Read bounded advisory unit context, decisions and blockers.") { resumeId };
        resume.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.TaskResume(parse.GetValue(resumeId)!), ct)));
        task.Subcommands.Add(resume);
    }

    internal static async Task<T> ReadFile<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) where T : class =>
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, ct), type) ?? throw new JsonException("Provide a JSON object, not null.");
}
