using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class TaskUnitTests
{
    private sealed class Fixture : IDisposable
    {
        public HubFactory Hub { get; } = new();
        public TestRepo Repo { get; } = new();
        public HttpClient Owner { get; private set; } = null!;
        public string Id { get; private set; } = "";
        public string Base => Repo.Git("rev-parse", "integration");
        public string Blob => Repo.Git("rev-parse", "integration:specs/T-1.md");
        public TaskUnitGraph Graph { get; private set; } = null!;

        public async Task Initialize()
        {
            await Hub.AddProjectAsync(repoPath: Repo.Path, validators: ["reviewer"]);
            Owner = await Hub.RegisterAgentAsync("owner");
            Id = (await Owner.AddTaskAsync("Checkpoint fixture")).Id;
            (await Owner.ClaimAsync(Id)).EnsureSuccessStatusCode();
            Repo.Git("checkout", "-b", "integration");
            Repo.WriteSpec(Id);
            Repo.Commit("frozen spec");
            (await Owner.PostActionAsync(Id, "spec", new SetSpecRequest($"specs/{Id}.md", "integration"))).EnsureSuccessStatusCode();
            Graph = await Read(await Owner.PostAsJsonAsync(Routes.TaskUnits(Id) + "/define", Definition()));
        }

        public DefineTaskUnitsRequest Definition(long revision = 0) => new(revision, Blob, "integration",
            [new("a", [], ["test"]), new("b", [], ["test"]), new("c", ["a"], ["test"])]);
        public TaskUnit Unit(string id) => Graph.Units.Single(x => x.Id == id);
        public TaskUnitCheckpointRequest Request(string unit, string action) => new(Graph.Revision, unit, Unit(unit).Attempt?.AttemptId ?? Guid.NewGuid(), action);
        public async Task<TaskUnitGraph> Apply(TaskUnitCheckpointRequest request)
        {
            Graph = await Read(await Owner.PostAsJsonAsync(Routes.TaskUnits(Id) + "/checkpoint", request));
            return Graph;
        }
        public Task<HttpResponseMessage> Post(TaskUnitCheckpointRequest request) => Owner.PostAsJsonAsync(Routes.TaskUnits(Id) + "/checkpoint", request);
        public async Task Start(string unit)
        {
            await Apply(new(Graph.Revision, unit, Guid.NewGuid(), "start", BaseCommit: Base, OutputBranch: "worker/" + unit));
            Repo.Git("checkout", "-B", "worker/" + unit, Base);
        }
        public string Output(string unit)
        {
            Repo.Write(unit + ".txt", unit);
            Repo.Commit(unit + " output");
            return Repo.Git("rev-parse", "HEAD");
        }
        public TaskUnitCheckpointRequest Proof(string unit)
        {
            Repo.Write("evidence/" + unit + ".txt", "test exit=0; reviewed diff");
            Repo.Commit(unit + " evidence");
            var commit = Repo.Git("rev-parse", "HEAD");
            return Request(unit, "verify") with { Checks = [new("test", 0, "evidence/" + unit + ".txt", commit)],
                ReviewEvidencePath = "evidence/" + unit + ".txt", ReviewEvidenceCommit = commit };
        }
        public async Task Accept(string unit)
        {
            await Start(unit);
            await Apply(Request(unit, "report") with { OutputCommit = Output(unit) });
            await Apply(Proof(unit));
        }
        public async Task Integrate(string unit)
        {
            Repo.Git("checkout", "integration");
            Repo.Git("merge", "--ff-only", Unit(unit).Attempt!.OutputCommit!);
            await Apply(Request(unit, "integrate"));
        }
        public void Dispose() { Owner?.Dispose(); Hub.Dispose(); Repo.Dispose(); }
    }

    private static async Task<TaskUnitGraph> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.TaskUnitGraph))!;
    }

    private static async Task Error(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Report_review_integration_and_exact_replays_are_separate_durable_transitions()
    {
        using var f = new Fixture();
        await f.Initialize();
        var replay = await Read(await f.Owner.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/define", f.Definition(f.Graph.Revision)));
        Assert.Equal(f.Graph.Revision, replay.Revision);
        await f.Start("a");
        var report = f.Request("a", "report") with { OutputCommit = f.Output("a") };
        await f.Apply(report);
        Assert.Equal("pending", f.Unit("a").Attempt!.ReviewState);
        Assert.Null(f.Unit("a").Attempt!.IntegrationCommit);
        var revision = f.Graph.Revision;
        await f.Apply(report with { ExpectedRevision = revision });
        Assert.Equal(revision, f.Graph.Revision);
        await Error(await f.Post(f.Request("a", "integrate")), "unit_not_accepted");
        var proof = f.Proof("a");
        await f.Apply(proof);
        Assert.Equal("accepted", f.Unit("a").Attempt!.ReviewState);
        revision = f.Graph.Revision;
        await f.Apply(proof with { ExpectedRevision = revision });
        Assert.Equal(revision, f.Graph.Revision);
        await f.Integrate("a");
        revision = f.Graph.Revision;
        await f.Apply(f.Request("a", "integrate"));
        Assert.Equal(revision, f.Graph.Revision);
        await f.Apply(f.Request("a", "reconcile"));
        Assert.Equal("accepted", f.Unit("a").Attempt!.ReviewState);
        Assert.Single(f.Unit("a").Attempt!.Checks);
        var detail = await f.Owner.GetTaskAsync(f.Id);
        Assert.Equal(TaskState.InProgress, detail.Task.State);
        Assert.Contains(detail.Events, e => e.Type == "task.unit_verified");
    }

    [Fact]
    public async Task Recovery_finds_commits_and_merges_without_auto_review_and_survives_restart()
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Start("a");
        await f.Apply(f.Request("a", "reconcile"));
        Assert.Equal("running", f.Unit("a").Attempt!.ReportedState);
        var output = f.Output("a");
        await f.Apply(f.Request("a", "reconcile"));
        Assert.Equal(output, f.Unit("a").Attempt!.OutputCommit);
        Assert.Equal("recovered", f.Unit("a").Attempt!.ReportedState);
        Assert.Equal("pending", f.Unit("a").Attempt!.ReviewState);
        await f.Apply(f.Proof("a"));
        f.Repo.Git("checkout", "integration");
        f.Repo.Git("merge", "--ff-only", output);
        var token = f.Hub.FounderToken;
        using var restart = new HubFactory { DataDir = f.Hub.DataDir };
        var client = restart.CreateClient(token);
        var graph = await Read(await client.GetAsync(Routes.TaskUnits(f.Id)));
        Assert.Equal(f.Graph.Revision, graph.Revision);
        graph = await Read(await client.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/checkpoint", f.Request("a", "reconcile")));
        Assert.Equal(f.Base, graph.Units[0].Attempt!.IntegrationCommit);
        Assert.Equal("accepted", graph.Units[0].Attempt!.ReviewState);
    }

    [Fact]
    public async Task Cas_old_attempt_reuse_and_dependency_invalidation_preserve_independent_outputs()
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Accept("a");
        await f.Integrate("a");
        await f.Accept("b");
        await f.Integrate("b");
        await f.Accept("c");
        var b = f.Unit("b");
        var old = f.Request("a", "report");
        await Error(await f.Post(old with { ExpectedRevision = 0 }), "checkpoint_conflict", HttpStatusCode.Conflict);
        await f.Apply(f.Request("a", "start") with { AttemptId = Guid.NewGuid(), BaseCommit = f.Base, OutputBranch = "worker/a2", Reason = "replacement" });
        Assert.Equal(b, f.Unit("b"));
        Assert.Equal("rejected", f.Unit("c").Attempt!.ReviewState);
        Assert.Null(f.Unit("c").Attempt!.IntegrationCommit);
        Assert.NotNull(f.Unit("c").Attempt!.OutputCommit);
        await Error(await f.Post(old with { ExpectedRevision = f.Graph.Revision }), "stale_attempt", HttpStatusCode.Conflict);
        await Error(await f.Post(old with { ExpectedRevision = f.Graph.Revision, Action = "start", BaseCommit = f.Base, OutputBranch = "worker/a3", Reason = "reuse" }), "stale_attempt", HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("missing-check")]
    [InlineData("duplicate-check")]
    [InlineData("failed-check")]
    [InlineData("missing-review")]
    [InlineData("missing-file")]
    [InlineData("stale-evidence")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("revision")]
    public async Task Verification_refuses_incomplete_stale_or_unsafe_proof(string fault)
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Start("a");
        await f.Apply(f.Request("a", "report") with { OutputCommit = f.Output("a") });
        var proof = f.Proof("a");
        var check = proof.Checks![0];
        proof = fault switch
        {
            "missing-check" => proof with { Checks = [] },
            "duplicate-check" => proof with { Checks = [check, check] },
            "failed-check" => proof with { Checks = [check with { ExitCode = 1 }] },
            "missing-review" => proof with { ReviewEvidenceCommit = null },
            "missing-file" => proof with { ReviewEvidencePath = "absent.txt" },
            "stale-evidence" => proof with { ReviewEvidenceCommit = f.Base },
            "traversal" => proof with { ReviewEvidencePath = "../evidence/a.txt" },
            "absolute" => proof with { ReviewEvidencePath = "C:/evidence/a.txt" },
            _ => proof with { ReviewEvidenceCommit = "HEAD~1" }
        };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await f.Post(proof)).StatusCode);
        var graph = await Read(await f.Owner.GetAsync(Routes.TaskUnits(f.Id)));
        Assert.Equal(f.Graph.Revision, graph.Revision);
        Assert.Equal("pending", graph.Units[0].Attempt!.ReviewState);
    }

    [Theory]
    [InlineData("missing-branch")]
    [InlineData("rewritten-output")]
    [InlineData("spec")]
    [InlineData("base")]
    public async Task Reconcile_durably_invalidates_stale_git_proof(string fault)
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Accept("a");
        f.Repo.Git("checkout", "integration");
        if (fault == "missing-branch") f.Repo.Git("branch", "-D", "worker/a");
        if (fault == "rewritten-output") f.Repo.Git("branch", "-f", "worker/a", "main");
        if (fault == "spec") { f.Repo.Write("specs/T-1.md", "# T-1 amended\n"); f.Repo.Commit("amend spec"); }
        if (fault == "base") f.Repo.Git("reset", "--hard", "main");
        await f.Apply(f.Request("a", "reconcile"));
        Assert.Equal("rejected", f.Unit("a").Attempt!.ReviewState);
        Assert.NotNull(f.Unit("a").InvalidationReason);
        Assert.Null(f.Unit("a").Attempt!.IntegrationCommit);
    }

    [Fact]
    public async Task Definitions_are_atomic_and_spec_changes_start_fresh()
    {
        using var f = new Fixture();
        await f.Initialize();
        var definitions = new IReadOnlyList<TaskUnitDefinition>[]
        {
            [new("A", [], [])], [new("a", [], []), new("a", [], [])], [new("a", ["absent"], [])],
            [new("a", ["b"], []), new("b", ["a"], [])], [new("a", ["a"], [])], [new("a", [], ["x", "x"])]
        };
        foreach (var units in definitions)
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await f.Owner.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/define", f.Definition(f.Graph.Revision) with { Units = units })).StatusCode);
        await Error(await f.Owner.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/define", f.Definition(f.Graph.Revision) with { Units = [new("other", [], [])] }), "unit_definition_changed");
        await f.Accept("a");
        f.Repo.Git("checkout", "integration");
        f.Repo.Write("specs/T-1.md", "# T-1 changed\n");
        f.Repo.Commit("amended definition");
        var graph = await Read(await f.Owner.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/define", f.Definition(f.Graph.Revision)));
        Assert.All(graph.Units, x => Assert.Null(x.Attempt));
        Assert.Equal(f.Graph.Revision + 1, graph.Revision);
    }

    [Fact]
    public async Task Mutations_require_owner_live_claim_and_in_progress_even_for_founder()
    {
        using var f = new Fixture();
        await f.Initialize();
        var request = f.Request("a", "start") with { BaseCommit = f.Base, OutputBranch = "worker/a" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Hub.CreateClient().PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/checkpoint", request)).StatusCode);
        var other = await f.Hub.RegisterAgentAsync("other");
        await Error(await other.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/checkpoint", request), "not_owner");
        f.Hub.Clock.Advance(TimeSpan.FromHours(1));
        await Error(await f.Post(request), "unit_owner_inactive");
        await Read(await f.Hub.Founder().PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/checkpoint", request));
        (await f.Hub.Founder().PostActionAsync(f.Id, "release", new ReleaseTaskRequest())).EnsureSuccessStatusCode();
        await Error(await f.Hub.Founder().PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/define", f.Definition(2)), "unit_owner_inactive");
    }

    [Fact]
    public async Task Lost_evidence_blob_invalidates_review_even_when_its_tree_entry_survives()
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Accept("a");
        var blob = f.Repo.Git("rev-parse", "worker/a:evidence/a.txt");
        var file = Path.Combine(f.Repo.Path, ".git", "objects", blob[..2], blob[2..]);
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
        await f.Apply(f.Request("a", "reconcile"));
        Assert.Equal("rejected", f.Unit("a").Attempt!.ReviewState);
        Assert.Contains("missing", f.Unit("a").InvalidationReason);
    }

    [Fact]
    public async Task Symlink_evidence_and_spec_are_refused_without_reading_the_target()
    {
        using var f = new Fixture();
        await f.Initialize();
        await f.Start("a");
        await f.Apply(f.Request("a", "report") with { OutputCommit = f.Output("a") });
        var blob = f.Repo.Git("rev-parse", "HEAD:a.txt");
        f.Repo.Git("update-index", "--add", "--cacheinfo", "120000," + blob + ",link");
        f.Repo.Git("commit", "-qm", "committed symlink");
        var proof = f.Proof("a") with { ReviewEvidencePath = "link" };
        await Error(await f.Post(proof), "unit_artifact_missing");
        f.Repo.Git("checkout", "integration");
        f.Repo.Git("update-index", "--add", "--cacheinfo", "120000," + blob + ",specs/T-1.md");
        f.Repo.Git("commit", "-qm", "symlink spec");
        await Error(await f.Owner.PostAsJsonAsync(Routes.TaskUnits(f.Id) + "/define", f.Definition(f.Graph.Revision)), "unit_artifact_missing");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("main")]
    [InlineData("integration")]
    [InlineData("bad..branch")]
    public async Task Dispatch_refuses_unsafe_or_reserved_output_branches(string branch)
    {
        using var f = new Fixture();
        await f.Initialize();
        await Error(await f.Post(f.Request("a", "start") with { BaseCommit = f.Base, OutputBranch = branch }), "invalid_unit_branch");
    }
}
