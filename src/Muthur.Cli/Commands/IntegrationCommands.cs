using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

public static class IntegrationCommands
{
    public static void AddTo(RootCommand root)
    {
        var integration = new Command("integration", "Check the exact combined result before landing, without model calls.");
        root.Subcommands.Add(integration);
        foreach (var name in new[] { "show", "run" })
        {
            var task = new Argument<string>("task");
            var assignmentFile = new Option<string?>("--assignment-file") { Description = "Conductor-issued immutable assignment file." };
            var command = new Command(name, name == "show" ? "Show current and historical candidates." : "Claim and run the approved integration checks.") { task };
            if (name == "run") command.Options.Add(assignmentFile);
            command.SetAction(async (parse, ct) =>
            {
                var id = parse.GetValue(task)!;
                var hub = HubClient.For(parse);
                if (name == "show") return Output.Emit(parse, await hub.GetAsync(Routes.TaskIntegration(id), ct));
                try
                {
                    var file = parse.GetValue(assignmentFile);
                    var assignment = file is null ? null : JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, ct), MuthurJsonContext.Default.IntegrationAssignmentDto);
                    var result = await new IntegrationRunner(new ProcessRunner(), new Client(hub)).RunAsync(id, ct, assignment);
                    Output.Emit(parse, await hub.GetAsync(Routes.TaskIntegration(id), ct));
                    return result.Passed ? 0 : Output.Error("integration_failed", $"{result.Failure ?? "A required check failed."} Evidence: {result.EvidencePath}", 2);
                }
                catch (ApiException ex) { return Output.Emit(parse, ex.Result); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                { return Output.Error("integration_runner_failed", ex.Message, 2); }
            });
            integration.Subcommands.Add(command);
        }
    }

    private sealed class ApiException(ApiResult result) : IOException(result.Body)
    {
        public ApiResult Result { get; } = result;
    }

    private sealed class Client(HubClient hub) : IIntegrationClient
    {
        private async Task<ApiResult> Post<T>(string task, string action, T body, JsonTypeInfo<T> json, CancellationToken ct)
        {
            var response = await hub.PostAsync(Routes.TaskIntegrationAction(task, action), body, json, ct);
            if (!response.IsSuccess) throw new ApiException(response);
            return response;
        }
        public async Task<IntegrationAssignmentDto> ClaimAsync(string task, CancellationToken ct) =>
            JsonSerializer.Deserialize((await Post(task, "claim", new IntegrationClaimRequest(), MuthurJsonContext.Default.IntegrationClaimRequest, ct)).Body,
                MuthurJsonContext.Default.IntegrationAssignmentDto)!;
        public async Task StartedAsync(string task, IntegrationStartRequest r, CancellationToken ct) => _ = await Post(task, "started", r, MuthurJsonContext.Default.IntegrationStartRequest, ct);
        public async Task CandidateAsync(string task, IntegrationCandidateRequest r, CancellationToken ct) => _ = await Post(task, "candidate", r, MuthurJsonContext.Default.IntegrationCandidateRequest, ct);
        public async Task VerdictAsync(string task, IntegrationEvidenceDto r, CancellationToken ct) => _ = await Post(task, "verdict", r, MuthurJsonContext.Default.IntegrationEvidenceDto, ct);
        public async Task FailureAsync(string task, IntegrationFailureRequest r, CancellationToken ct) => _ = await Post(task, "failure", r, MuthurJsonContext.Default.IntegrationFailureRequest, ct);
        public async Task RenewAsync(string task, IntegrationRenewRequest r, CancellationToken ct) => _ = await Post(task, "renew", r, MuthurJsonContext.Default.IntegrationRenewRequest, ct);
    }
}
