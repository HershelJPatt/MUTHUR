using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class IntegrationPersistenceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Upgrade_preserves_existing_task_subject_and_verdict_without_a_candidate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MuthurDb>().UseSqlite(connection).Options;
        var projectId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        await using (var db = new MuthurDb(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260921031130_ValidationSubjects");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO projects (id, key, name, repo_path, default_branch, land_mode, required_validators, ingest_sources, created_at)
                VALUES ({{projectId}}, 'upgrade', 'Upgrade', '/repo', 'main', 'Merge', '[]', '[]', 0);
                INSERT INTO validation_subjects (id, task_id, project_id, repository_path, implementation_sha, spec_path, spec_sha256,
                    required_validators_json, required_checks_json, invalidating_environment_json, descriptive_metadata_json, created_at)
                VALUES ({{subjectId}}, 105, {{projectId}}, '/repo', 'implementation', 'specs/T-105.md', 'digest', '[]', '{}', '{}', '{}', 0);
                INSERT INTO tasks (id, project_id, title, body, state, priority, depends_on, created_at, updated_at, current_subject_id)
                VALUES (105, {{projectId}}, 'Existing task', 'Preserved body', 'Validated', 0, '[]', 0, 0, {{subjectId}});
                INSERT INTO task_validations (task_id, validator_key, verdict, waiting_since, subject_id, evidence)
                VALUES (105, 'review', 'Yes', 0, {{subjectId}}, 'Preserved independent evidence');
                """);
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        await using var fresh = new MuthurDb(options);
        var task = await fresh.Tasks.SingleAsync();
        Assert.Equal("Existing task", task.Title);
        Assert.Equal("Preserved body", task.Body);
        Assert.Equal(subjectId, task.CurrentSubjectId);
        Assert.Equal("implementation", task.CurrentSubject!.ImplementationSha);
        Assert.Null(task.CurrentIntegrationCandidateId);
        Assert.Null(task.CurrentIntegrationCandidate);
        Assert.Equal("Preserved independent evidence", (await fresh.TaskValidations.SingleAsync()).Evidence);
        Assert.Empty(await fresh.IntegrationCandidates.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_survives_a_fresh_context_with_typed_mapping(bool founder)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MuthurDb>().UseSqlite(connection).Options;
        var candidate = Candidate();
        var evidence = Evidence(candidate.AssignmentId, candidate.Id, candidate.SubjectId);
        var failure = new IntegrationFailureRequest(candidate.AssignmentId, candidate.SubjectId, "launch", "runner_failed", "Check failed",
            "logs/launch", "failure-digest", ["conflict.cs"], ["T-104"]);
        candidate.EvidenceJson = JsonSerializer.Serialize(evidence, MuthurJsonContext.Default.IntegrationEvidenceDto);
        candidate.FailureJson = JsonSerializer.Serialize(failure, MuthurJsonContext.Default.IntegrationFailureRequest);
        candidate.FailureCode = failure.Code;
        candidate.FailureMessage = failure.Message;
        candidate.AssignedAgentId = founder ? null : Guid.NewGuid();
        await using (var db = new MuthurDb(options))
        {
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project { Id = candidate.ProjectId, Key = "test", Name = "Test", RepoPath = candidate.RepositoryPath });
            db.Tasks.Add(new WorkTask { Id = candidate.TaskId, ProjectId = candidate.ProjectId, Title = "Candidate task" });
            db.ValidationSubjects.Add(new ValidationSubject
            {
                Id = candidate.SubjectId, TaskId = candidate.TaskId, ProjectId = candidate.ProjectId,
                RepositoryPath = candidate.RepositoryPath, ImplementationSha = candidate.ImplementationSha,
                SpecPath = "specs/T-105.md", SpecSha256 = "digest", RequiredValidatorsJson = "[]",
                RequiredChecksJson = candidate.RequiredChecksJson, InvalidatingEnvironmentJson = "{}", DescriptiveMetadataJson = "{}"
            });
            if (candidate.AssignedAgentId is { } agentId)
                db.Agents.Add(new Agent { Id = agentId, Name = "runner", TokenHash = "hash", Harness = "local", Model = "none" });
            await db.SaveChangesAsync();
            db.IntegrationCandidates.Add(candidate);
            await db.SaveChangesAsync();
            (await db.Tasks.SingleAsync()).CurrentIntegrationCandidateId = candidate.Id;
            await db.SaveChangesAsync();
        }
        await using var fresh = new MuthurDb(options);
        var task = await fresh.Tasks.SingleAsync();
        var loaded = Assert.IsType<IntegrationCandidate>(task.CurrentIntegrationCandidate);
        Assert.Equal(JsonSerializer.Serialize(candidate.ToDto(), MuthurJsonContext.Default.IntegrationCandidateDto),
            JsonSerializer.Serialize(loaded.ToDto(), MuthurJsonContext.Default.IntegrationCandidateDto));
        Assert.Equal(candidate.EvidenceJson, loaded.EvidenceJson);
        Assert.Equal(candidate.FailureJson, loaded.FailureJson);
        Assert.Equal(candidate.RequiredChecksJson, loaded.RequiredChecksJson);
        Assert.Equal(candidate.Id, task.ToDto().CurrentIntegrationCandidate!.Id);
        Assert.Equal(candidate.AssignmentId, loaded.AssignmentId);
        Assert.NotEqual(loaded.Id, loaded.AssignmentId);
        Assert.Equal(At.AddMinutes(1), loaded.StartedAt);
        Assert.Equal(At.AddMinutes(2), loaded.CompletedAt);
        Assert.Equal(At.AddMinutes(3), loaded.PromotionIntentAt);
        Assert.Equal(At.AddMinutes(4), loaded.PromotedAt);
        Assert.All(fresh.Model.FindEntityType(typeof(IntegrationCandidate))!.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        Assert.All(fresh.Model.FindEntityType(typeof(IntegrationCandidate))!.GetNavigations(), navigation => Assert.False(navigation.IsEagerLoaded));
    }

    [Fact]
    public void Source_generated_metadata_round_trips_requests_reports_and_history()
    {
        var candidate = Candidate();
        var evidence = Evidence(candidate.AssignmentId, candidate.Id, candidate.SubjectId);
        RoundTrip(new IntegrationClaimRequest(), MuthurJsonContext.Default.IntegrationClaimRequest);
        RoundTrip(new IntegrationCandidateRequest(candidate.AssignmentId, candidate.SubjectId, "target", "implementation", "candidate", "tree"), MuthurJsonContext.Default.IntegrationCandidateRequest);
        RoundTrip(new IntegrationRenewRequest(candidate.AssignmentId, candidate.SubjectId), MuthurJsonContext.Default.IntegrationRenewRequest);
        RoundTrip(evidence, MuthurJsonContext.Default.IntegrationEvidenceDto);
        RoundTrip(new IntegrationFailureRequest(candidate.AssignmentId, candidate.SubjectId, "construction", "merge_conflict", "Conflict",
            "merge.log", "digest", ["file.cs"], ["T-1"]), MuthurJsonContext.Default.IntegrationFailureRequest);
        foreach (var state in new[] { "assigned", "checking", "passed", "failed", "invalidated", "interrupted", "promoted" })
        {
            var dto = candidate.ToDto() with { State = state, Evidence = evidence };
            var json = RoundTrip(dto, MuthurJsonContext.Default.IntegrationCandidateDto);
            Assert.Contains($"\"state\":\"{state}\"", json, StringComparison.Ordinal);
            RoundTrip(new IntegrationHistoryDto(dto, [dto]), MuthurJsonContext.Default.IntegrationHistoryDto);
            RoundTrip(new IntegrationAssignmentDto(dto, 600), MuthurJsonContext.Default.IntegrationAssignmentDto);
        }
        RoundTrip(new IntegrationHistoryDto(null, []), MuthurJsonContext.Default.IntegrationHistoryDto);
        var defaults = JsonSerializer.Deserialize("""{"assignmentId":"00000000-0000-0000-0000-000000000001","subjectId":"00000000-0000-0000-0000-000000000002","targetSha":"t","implementationSha":"i","candidateSha":"c","treeSha":"tree"}""", MuthurJsonContext.Default.IntegrationCandidateRequest);
        Assert.False(defaults!.AlreadyIncluded);
        foreach (var action in new[] { "claim", "candidate", "verdict", "failure", "renew" })
            Assert.Equal(Routes.Task("T-105") + "/integration/" + action, Routes.TaskIntegrationAction("T-105", action));
    }

    private static string RoundTrip<T>(T value, JsonTypeInfo<T> type)
    {
        var json = JsonSerializer.Serialize(value, type);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize(json, type)!, type));
        return json;
    }

    private static IntegrationEvidenceDto Evidence(Guid assignment, Guid candidate, Guid subject) =>
        new(assignment, candidate, subject, "candidate",
            [new("build", "dotnet build", 1, 123, "build.log", "digest"), new("test", "dotnet test", null, 0, null, null, true)],
            At, At.AddMinutes(2), true, "Owned worktree removed");

    private static IntegrationCandidate Candidate() => new()
    {
        Id = Guid.NewGuid(), AssignmentId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), TaskId = 105, SubjectId = Guid.NewGuid(),
        RepositoryPath = Path.GetFullPath("."), DefaultBranch = "main", TargetSha = "target", ImplementationSha = "implementation",
        CandidateSha = "candidate", TreeSha = "tree", RequiredChecksJson = """{"build":"dotnet build","test":"dotnet test"}""",
        State = "passed", LeaseExpires = At.AddMinutes(10), Attempt = 2, CreatedAt = At, StartedAt = At.AddMinutes(1),
        CompletedAt = At.AddMinutes(2), PromotionIntentAt = At.AddMinutes(3), PromotedAt = At.AddMinutes(4),
        AlreadyIncluded = true, EvidenceSha256 = "evidence-digest"
    };
}
