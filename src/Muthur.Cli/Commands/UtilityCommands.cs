using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

public static class UtilityCommands
{
    public static void AddTo(RootCommand root)
    {
        var utility = new Command("utility", "Bounded local summaries: one request, no tools or cloud fallback.");
        var file = new Option<string>("--file") { Required = true, Description = "Evidence file; only a bounded excerpt is sent to local Ollama." };
        var model = new Option<string?>("--model") { Description = "Choose an installed model from the utility tier for comparison." };
        var task = new Option<string?>("--task") { Description = "Associate measured usage with this task." };
        var summarize = new Command("summarize", "Summarize a log or incoming text locally; output includes token counts and elapsed time.") { file, model, task };
        summarize.SetAction(async (parse, ct) =>
        {
            var hub = HubClient.For(parse);
            var response = await hub.GetAsync($"{Routes.Tiers}?tier=utility", ct);
            if (!response.IsSuccess) return Output.Emit(parse, response);
            var tiers = JsonSerializer.Deserialize(response.Body, MuthurJsonContext.Default.IReadOnlyListTierDto) ?? [];
            var selected = tiers.SelectMany(t => t.Candidates).FirstOrDefault(c => !c.Limited && c.Account == "local" &&
                (parse.GetValue(model) is not { } requested || c.Model == requested));
            if (selected is null) return Output.Error("no_local_model", "No matching local utility candidate. Inspect muthur harness tiers.", ExitCodes.RuleViolation);
            var path = Path.GetFullPath(parse.GetValue(file)!);
            if (!File.Exists(path)) return Output.Error("file_missing", path, ExitCodes.RuleViolation);
            // Refuse excessive files before reading them; callers should extract relevant errors, not dump a repository.
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                return Output.Error("input_too_large", "Extract a relevant log section smaller than 4 MiB first.", ExitCodes.RuleViolation);
            var clock = Stopwatch.StartNew();
            LocalSummaryResult? result = null;
            string? error = null;
            try
            {
                using var lease = LocalInferenceLease.Acquire(MuthurEnvironment.Home);
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
                var endpoint = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://127.0.0.1:11434");
                result = await LocalSummary.RunAsync(client, endpoint, selected.Model, await File.ReadAllTextAsync(path, ct), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or ArgumentException or IOException)
            {
                error = ex.Message;
            }
            var recorded = await hub.PostAsync(Routes.WorkerRuns,
                new WorkerRunReport(parse.GetValue(task), "utility", "ollama", selected.Model, "local", "", "summary",
                    result is not null, (int)clock.Elapsed.TotalSeconds, 0, InputTokens: result?.InputTokens, OutputTokens: result?.OutputTokens),
                MuthurJsonContext.Default.WorkerRunReport, ct);
            if (!recorded.IsSuccess) return Output.Emit(parse, recorded);
            if (result is null) return Output.Error("local_summary_failed", $"{error} No retry or cloud fallback was attempted; use the original evidence.");
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("model", result.Model);
                json.WriteString("source", path);
                json.WriteString("summary", result.Summary);
                json.WriteBoolean("truncated", result.Truncated);
                json.WriteNumber("inputTokens", result.InputTokens);
                json.WriteNumber("outputTokens", result.OutputTokens);
                json.WriteNumber("seconds", clock.Elapsed.TotalSeconds);
                json.WriteBoolean("advisory", true);
                json.WriteEndObject();
            }
            return Output.Emit(parse, new ApiResult(200, Encoding.UTF8.GetString(buffer.ToArray())));
        });
        utility.Subcommands.Add(summarize);
        root.Subcommands.Add(utility);
    }
}
