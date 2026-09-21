using System.Diagnostics;
using Muthur.Contracts;

namespace Muthur.Server.Tests.Benchmark;

public sealed class WorkflowBenchmarkTests
{
    [Fact]
    public void Subject_binding_uses_the_captured_response_and_marks_legacy_absence()
    {
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();
        var first = System.Text.Json.JsonSerializer.SerializeToElement(new { currentSubject = new { id = firstId } });
        var second = System.Text.Json.JsonSerializer.SerializeToElement(new { currentSubject = new { id = secondId } });
        var captured = WorkflowScenarios.SubjectId(first);
        Assert.Equal(secondId, WorkflowScenarios.SubjectId(second));
        Assert.Equal(firstId, captured);
        Assert.Null(WorkflowScenarios.SubjectId(System.Text.Json.JsonSerializer.SerializeToElement(new { id = "T-1" })));
    }

    [Theory]
    [InlineData("{\"currentSubject\":null}")]
    [InlineData("{\"currentSubject\":{}}")]
    [InlineData("{\"currentSubject\":{\"id\":\"invalid\"}}")]
    public void Malformed_subject_evidence_is_not_treated_as_legacy(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => WorkflowScenarios.SubjectId(document.RootElement));
    }

    [Fact]
    [Trait("Category", "WorkflowBenchmark")]
    public async Task Scripted_workflows_are_graded_from_retained_evidence()
    {
        var (suite, hash) = WorkflowReport.LoadSuite();
        var selection = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_SCENARIO");
        var scenarios = suite.Scenarios.Where(s => string.IsNullOrEmpty(selection) || s.Id == selection).ToArray();
        Assert.NotEmpty(scenarios);
        var trialText = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_TRIALS") ?? "1";
        Assert.True(int.TryParse(trialText, out var trials) && trials is >= 1 and <= 10);
        var root = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_OUTPUT")
            ?? Path.Combine(AppContext.BaseDirectory, "Benchmark/artifacts", Guid.NewGuid().ToString("n"));
        var runId = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_RUN") ?? Guid.NewGuid().ToString("n");
        var reports = new List<TrialReport>();
        foreach (var scenario in scenarios)
        for (var trial = 1; trial <= trials; trial++)
        {
            var report = new TrialReport
            {
                SuiteHash = hash, RunId = runId, ScenarioId = scenario.Id, TrialIndex = trial,
                ProductRevision = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_PRODUCT") ?? "unavailable: direct dotnet test; use pinned runner",
                DriverRevision = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_DRIVER") ?? "unavailable: direct dotnet test; use pinned runner",
                DriverContentHash = Environment.GetEnvironmentVariable("MUTHUR_BENCHMARK_DRIVER_HASH") ?? "unavailable: direct dotnet test; use pinned runner"
            };
            var watch = Stopwatch.StartNew();
            using (var workflow = new WorkflowScenarios(report))
            {
                try { await workflow.Run(scenario); }
                catch (Exception e) { report.Error = e.Message; }
                finally
                {
                    try { await workflow.Capture(); }
                    catch (Exception e) { report.Error = $"{report.Error} Evidence capture failed: {e.Message}"; }
                    watch.Stop();
                    report.WallSeconds = watch.Elapsed.TotalSeconds;
                    report.UtcEnd = DateTimeOffset.UtcNow;
                    report.Grade = WorkflowReport.Grade(report, scenario);
                    report.Status = report.Grade.Passed ? "complete" : "error";
                    var directory = Path.Combine(root, scenario.Id, trial.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    WorkflowReport.Write(Path.Combine(directory, "trial.json"), report);
                    WorkflowReport.Write(Path.Combine(directory, "http.json"), report.Http);
                    WorkflowReport.Write(Path.Combine(directory, "task-events.json"), new { report.Task, report.Tasks, report.Events });
                    WorkflowReport.Write(Path.Combine(directory, "git.json"), report.Git!);
                    if (report.Git?.Result is { } content) File.WriteAllText(Path.Combine(directory, "result.txt"), content);
                }
            }
            reports.Add(report);
            WorkflowReport.Aggregate(root, reports, scenarios.Length * trials);
        }
        Assert.All(reports, r => Assert.True(r.Grade!.Passed, $"{r.ScenarioId}: {r.Error ?? "external grade failed"}; evidence: {root}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("41\n")]
    [InlineData("42\n\n")]
    [InlineData(" 42\n")]
    public void Worker_success_and_done_cannot_override_missing_or_wrong_product(string? content)
    {
        var (report, scenario) = Fixture("stale-spec", content);
        Assert.False(WorkflowReport.Grade(report, scenario).Passed);
        Assert.Equal(0, WorkflowReport.Grade(report, scenario).SuccessfulOutcomes);
    }

    [Fact]
    public void Missing_evidence_and_mutated_success_claims_fail_closed()
    {
        var (report, scenario) = Fixture("stale-spec", "42\n");
        Assert.True(WorkflowReport.Grade(report, scenario).Passed);
        report.Grade = new(true, true, false, false, 1);
        report.Events = null;
        Assert.False(WorkflowReport.Grade(report, scenario).Passed);
        report.Events = [new(1, DateTimeOffset.UnixEpoch, "script", null, "task.landed", "T-1", default)];
        report.Checks.Clear();
        Assert.False(WorkflowReport.Grade(report, scenario).Passed);
        foreach (var key in scenario.ExpectedOutcomes) report.Checks[key] = true;
        report.Git = report.Git! with { MainHead = "invalid" };
        Assert.False(WorkflowReport.Grade(report, scenario).Passed);
    }

    [Fact]
    public void Negative_control_is_detection_coverage_never_product_success()
    {
        var (report, scenario) = Fixture("false-green", "41\n");
        var grade = WorkflowReport.Grade(report, scenario);
        Assert.True(grade.Passed);
        Assert.True(grade.Detected);
        Assert.True(grade.FalseApproval);
        Assert.False(grade.ProductCorrect);
        Assert.Equal(0, grade.SuccessfulOutcomes);
        report.Git = report.Git! with { Result = null, ResultPresent = false };
        Assert.False(WorkflowReport.Grade(report, scenario).Passed);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("42\n")]
    [InlineData("42\r\n")]
    public void Exactly_one_terminal_newline_is_optional(string content)
    {
        var (report, scenario) = Fixture("stale-spec", content);
        Assert.True(WorkflowReport.Grade(report, scenario).Passed);
        report.Task = report.Task! with { Task = report.Task.Task with { State = TaskState.Validated } };
        Assert.False(WorkflowReport.Grade(report, scenario).Passed);
    }

    [Fact]
    public void Retention_redacts_credentials_without_erasing_usage_counts()
    {
        var text = WorkflowReport.Sanitize("{\"token\":\"secret\",\"nested\":[{\"authorization\":\"Bearer abc\"}],\"message\":\"Bearer def\",\"tokens\":null}");
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("abc", text);
        Assert.DoesNotContain("def", text);
        Assert.Contains("\"tokens\": null", text);
    }

    [Fact]
    public void Aggregate_separates_detection_from_success_and_marks_missing_trials_incomplete()
    {
        var (positive, positiveScenario) = Fixture("stale-spec", "42");
        var (negative, negativeScenario) = Fixture("false-green", "41");
        positive.Grade = WorkflowReport.Grade(positive, positiveScenario);
        negative.Grade = WorkflowReport.Grade(negative, negativeScenario);
        positive.Status = negative.Status = "complete";
        positive.WallSeconds = 2;
        negative.WallSeconds = 4;
        var root = Path.Combine(Path.GetTempPath(), "benchmark-report-" + Guid.NewGuid().ToString("n"));
        try
        {
            WorkflowReport.Aggregate(root, [positive, negative], 2);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "aggregate.json")));
            Assert.Equal(1, json.RootElement.GetProperty("successfulOutcomes").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("detections").GetInt32());
            Assert.Equal(3, json.RootElement.GetProperty("wallSeconds").GetProperty("mean").GetDouble());
            WorkflowReport.Aggregate(root, [positive, negative], 3);
            Assert.Contains("incomplete/error", File.ReadAllText(Path.Combine(root, "aggregate.json")));
            WorkflowReport.Aggregate(root, [positive, positive], 2);
            Assert.Contains("incomplete/error", File.ReadAllText(Path.Combine(root, "aggregate.json")));
            WorkflowReport.Aggregate(root, [], 2);
            Assert.Contains("incomplete/error", File.ReadAllText(Path.Combine(root, "aggregate.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static (TrialReport, Scenario) Fixture(string id, string? content)
    {
        var (suite, hash) = WorkflowReport.LoadSuite();
        var scenario = suite.Scenarios.Single(s => s.Id == id);
        var report = new TrialReport { ScenarioId = id, SuiteHash = hash, RunId = "test", ProductRevision = "test", DriverRevision = "test" };
        var task = new TaskDto("T-1", "demo", "scripted success", "", TaskState.Done, 0, "owner", null, "specs/T-1.md", "task/T-1", null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new("win-validator", "yes", "validator", "success", null, DateTimeOffset.UnixEpoch, null, null)], null);
        report.Task = new(task, []);
        report.Tasks = [task];
        report.Events = [new(1, DateTimeOffset.UnixEpoch, "script", null, "task.landed", "T-1", default)];
        report.Git = new(new string('a', 40), new string('b', 40), content, content is not null);
        report.Http.Add(new("GET", "/tasks/T-1", 200, default));
        foreach (var key in scenario.ExpectedOutcomes) report.Checks[key] = true;
        return (report, scenario);
    }
}
