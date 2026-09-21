using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Muthur.Contracts;

namespace Muthur.Server.Tests.Benchmark;

public sealed record Resources(int PaidSessions, int ModelCalls, int MaxScriptedStarts, double MaxFakeSeconds);
public sealed record Scenario(string Id, string Cohort, string SourceIncident, string InitialState,
    string[] ScriptedEvents, string[] ExpectedOutcomes, string[] FailureConditions, Resources Resources);
public sealed record ScenarioSuite(int Version, Scenario[] Scenarios);
public sealed record Phase(string Name, DateTimeOffset Start, DateTimeOffset End, bool Blocked);
public sealed record HttpEvidence(string Method, string Path, int Status, JsonElement Body);
public sealed record GitEvidence(string MainHead, string? BranchHead, string? Result, bool ResultPresent);
public sealed record Grade(bool Passed, bool ProductCorrect, bool FalseApproval, bool Detected, int SuccessfulOutcomes);

public sealed class TrialReport
{
    public int SchemaVersion { get; init; } = 1;
    public int SuiteVersion { get; init; } = 1;
    public required string SuiteHash { get; init; }
    public required string RunId { get; init; }
    public required string ProductRevision { get; init; }
    public required string DriverRevision { get; init; }
    public string DriverContentHash { get; init; } = "unavailable: direct test execution";
    public required string ScenarioId { get; init; }
    public string Cohort { get; init; } = "historical";
    public int TrialIndex { get; init; }
    public int SampleCount { get; init; } = 1;
    public DateTimeOffset UtcStart { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcEnd { get; set; }
    public double WallSeconds { get; set; }
    public List<Phase> FakePhases { get; } = [];
    public double BlockedSeconds => FakePhases.Where(p => p.Blocked).Sum(p => (p.End - p.Start).TotalSeconds);
    public List<HttpEvidence> Http { get; } = [];
    public TaskDetailDto? Task { get; set; }
    public IReadOnlyList<TaskDto>? Tasks { get; set; }
    public IReadOnlyList<EventDto>? Events { get; set; }
    public GitEvidence? Git { get; set; }
    public Dictionary<string, bool> Checks { get; } = [];
    public Grade? Grade { get; set; }
    public string Status { get; set; } = "incomplete";
    public string? Error { get; set; }
    public int StaffingAttempts { get; set; }
    public int ActualOsStarts { get; } = 0;
    public string ActualOsStartsScope { get; } = "agent/harness processes only; fake launchers start none; excludes git and test infrastructure";
    public int ScriptedLauncherInvocations { get; set; }
    public int WorkerRuns { get; set; }
    public string WorkerRunsScope { get; } = "scripted implementation submissions, not agent sessions";
    public int HumanInterventions { get; } = 0;
    public int PaidSessions { get; } = 0;
    public int ModelCalls { get; } = 0;
    public int? Tokens { get; } = null;
    public decimal? Cost { get; } = null;
    public string UsageUnavailableReason { get; } = "No model or paid harness is invoked in deterministic mode.";
    public string AgentBehavior { get; } = "unmeasured";
}

public static class WorkflowReport
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static (ScenarioSuite Suite, string Hash) LoadSuite()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Benchmark/scenarios.v1.json"));
        var suite = JsonSerializer.Deserialize<ScenarioSuite>(bytes, Json)!;
        if (suite.Version != 1 || suite.Scenarios.Length != 7 || suite.Scenarios.Select(s => s.Id).Distinct().Count() != 7)
            throw new InvalidDataException("Invalid scenario suite.");
        return (suite, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static string TrimTerminalNewline(string value) => value.EndsWith("\r\n", StringComparison.Ordinal)
        ? value[..^2] : value.EndsWith('\n') ? value[..^1] : value;

    public static Grade Grade(TrialReport report, Scenario scenario)
    {
        var evidence = report.ScenarioId == scenario.Id && report.Cohort == scenario.Cohort
            && report.Task is not null && report.Tasks is not null && report.Events is { Count: > 0 }
            && report.Git is { MainHead: var main, BranchHead: not null } heads
            && IsCommit(main) && IsCommit(heads.BranchHead)
            && report.Http.Count > 0 && report.Checks.Count == scenario.ExpectedOutcomes.Length
            && scenario.ExpectedOutcomes.All(k => report.Checks.GetValueOrDefault(k))
            && report.Error is null;
        var correct = report.Git is { ResultPresent: true, Result: not null } git && TrimTerminalNewline(git.Result) == "42";
        var done = report.Task?.Task.State == TaskState.Done;
        var approved = report.Task?.Task.Validations.Any(v => v.Verdict == "yes") == true;
        var falseApproval = done && approved && report.Git is { ResultPresent: true } && !correct;
        var negative = scenario.Id == "false-green";
        var bounded = scenario.Id == "no-verdict-loop";
        var resourceOk = report.PaidSessions == 0 && report.ModelCalls == 0
            && report.ScriptedLauncherInvocations <= scenario.Resources.MaxScriptedStarts
            && report.FakePhases.Sum(p => (p.End - p.Start).TotalSeconds) <= scenario.Resources.MaxFakeSeconds;
        var detected = evidence && negative && falseApproval;
        var pass = evidence && resourceOk && (negative ? detected : bounded
            ? report.Task!.Task.State == TaskState.Validating && !done
            : done && correct && approved);
        return new(pass, correct, falseApproval, detected, pass && !negative && !bounded ? 1 : 0);
    }

    public static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Sanitize(JsonSerializer.Serialize(value, Json)));
    }

    private static bool IsCommit(string value) => value.Length == 40 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Sanitize(string json)
    {
        var node = JsonNode.Parse(json);
        Redact(node);
        return Regex.Replace(node?.ToJsonString(Json) ?? "null", @"Bearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [redacted]", RegexOptions.IgnoreCase);
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (key.Equals("token", StringComparison.OrdinalIgnoreCase) || key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("access_token", StringComparison.OrdinalIgnoreCase) || key.Equals("founderToken", StringComparison.OrdinalIgnoreCase))
                    obj[key] = "[redacted]";
                else Redact(obj[key]);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) Redact(child);
    }

    public static void Aggregate(string root, IReadOnlyList<TrialReport> reports, int expected)
    {
        var complete = expected > 0 && reports.Count == expected && reports.All(r => r.Status == "complete" && r.Grade?.Passed == true)
            && reports.Select(r => (r.ScenarioId, r.TrialIndex)).Distinct().Count() == expected
            && reports.Select(r => (r.SuiteVersion, r.SuiteHash, r.Cohort, r.RunId, r.ProductRevision, r.DriverRevision, r.DriverContentHash)).Distinct().Count() == 1;
        Write(Path.Combine(root, "aggregate.json"), new
        {
            schemaVersion = 1, status = complete ? "complete" : "incomplete/error", expectedSamples = expected,
            sampleCount = reports.Count, suiteVersion = 1, suiteHash = reports.FirstOrDefault()?.SuiteHash,
            runId = reports.FirstOrDefault()?.RunId, productRevision = reports.FirstOrDefault()?.ProductRevision,
            driverRevision = reports.FirstOrDefault()?.DriverRevision, driverContentHash = reports.FirstOrDefault()?.DriverContentHash,
            cohort = "historical", forwardSampleCount = 0,
            scenarioIds = reports.Select(r => r.ScenarioId).Distinct().ToArray(),
            successfulOutcomes = reports.Sum(r => r.Grade?.SuccessfulOutcomes ?? 0),
            detections = reports.Count(r => r.Grade?.Detected == true),
            staffingAttempts = reports.Sum(r => r.StaffingAttempts), actualOsStarts = reports.Sum(r => r.ActualOsStarts),
            scriptedLauncherInvocations = reports.Sum(r => r.ScriptedLauncherInvocations), workerRuns = reports.Sum(r => r.WorkerRuns),
            humanInterventions = 0, paidSessions = 0, modelCalls = 0, tokens = (int?)null, cost = (decimal?)null,
            usageUnavailableReason = "No model calls; agent behavior unmeasured.", agentBehavior = "unmeasured",
            wallSeconds = new { min = reports.Select(r => (double?)r.WallSeconds).Min(), max = reports.Select(r => (double?)r.WallSeconds).Max(), mean = reports.Select(r => (double?)r.WallSeconds).Average() },
            trials = reports.Select(r => new { r.ScenarioId, r.TrialIndex, r.Status, r.Grade, evidence = $"{r.ScenarioId}/{r.TrialIndex}/trial.json" })
        });
        File.WriteAllText(Path.Combine(root, "report.md"),
            $"# Deterministic workflow benchmark\n\nStatus: {(complete ? "complete" : "incomplete/error")}. Agent behavior: unmeasured.\n\n" +
            "| Scenario | Trial | Passed | Product correct | Detection | Wall seconds | Blocked fake seconds |\n|---|---:|---|---|---|---:|---:|\n" +
            string.Join('\n', reports.Select(r => $"| {r.ScenarioId} | {r.TrialIndex} | {r.Grade?.Passed} | {r.Grade?.ProductCorrect} | {r.Grade?.Detected} | {r.WallSeconds:F3} | {r.BlockedSeconds} |")) +
            "\n\nWall time is a process stopwatch observation, not task-hours. No speedup claim. Tokens/cost unavailable. Paid sessions/model calls: 0.\n");
    }
}
