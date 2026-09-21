using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public sealed class DesignTests : IDisposable
{
    private readonly HubFactory hub = new();
    private readonly TestRepo repo = new();
    public void Dispose() { hub.Dispose(); repo.Dispose(); }

    [Fact]
    public async Task Approval_precedes_implementation_and_binds_artifacts_spec_and_independent_comparison()
    {
        await hub.AddProjectAsync(repoPath: repo.Path, validators: ["visual-validator"]);
        (await hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("visual-validator", "Review visual and interaction evidence."))).EnsureSuccessStatusCode();
        var owner = await hub.RegisterAgentAsync("designer");
        var validator = await hub.RegisterAgentAsync("reviewer");
        (await validator.PostAsync(Routes.RoleAction("visual-validator", "take"), null)).EnsureSuccessStatusCode();
        var task = await owner.AddTaskAsync("Review a visual change");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        repo.Git("checkout", "-q", "-b", "task/design");
        repo.Write("design/layout.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><title>Committed fixture layout</title></svg>");
        repo.Commit("visual fixture");
        var commit = repo.Git("rev-parse", "HEAD");
        var blob = repo.Git("rev-parse", "HEAD:design/layout.svg");
        var artifacts = new[] { new DesignArtifact("Before", "before", "success", commit, "design/layout.svg", blob) }
            .Concat(new[] { "loading", "empty", "error", "success", "narrow" }.Select(state => new DesignArtifact(state, "after", state, commit, "design/layout.svg", blob))).ToArray();
        var definition = new DesignDefinition("Layout comparison fixture", "Use existing tokens", "Keyboard and contrast review", "Compare all named states", artifacts);
        var path = "/api/v1/designs/" + task.Id;
        var bad = await owner.PostAsJsonAsync(path + "/attach", new WriteDesignRequest(0, definition with { Artifacts = [.. artifacts.Select(a => a with { Blob = new string('0', 40) })] }));
        Assert.Equal("design_artifact_changed", (await bad.ReadErrorAsync()).Code);
        var attached = await owner.PostAsJsonAsync(path + "/attach", new WriteDesignRequest(0, definition));
        attached.EnsureSuccessStatusCode();
        var design = (await attached.Content.ReadFromJsonAsync(MuthurJsonContext.Default.DesignDto))!;
        repo.Write("specs/" + task.Id + ".md", "# " + task.Id + " visual change\ndesign: " + design.Revisions[0].Sha256 + "\n");
        repo.Commit("bind spec to design");
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest("specs/" + task.Id + ".md", "task/design"))).EnsureSuccessStatusCode();
        var approve = new ApproveDesignRequest(1, true, "Fixture founder preference decision", repo.Git("rev-parse", "HEAD"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync(path + "/approve", approve)).StatusCode);
        (await hub.Founder().PostAsJsonAsync(path + "/approve", approve)).EnsureSuccessStatusCode();
        task = await (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest("task/design"))).ReadTaskAsync();
        var vote = new VerdictRequest("visual-validator", "Inspected committed layout and keyboard interaction fixture.", task.CurrentSubject!.Id);
        Assert.Equal("design_comparison_required", (await (await validator.PostActionAsync(task.Id, "pass", vote)).ReadErrorAsync()).Code);
        var check = new CheckDesignRequest(1, task.CurrentSubject.Id, false, "Layout differs from the approved spacing.", "Keyboard focus remains visible.");
        (await validator.PostAsJsonAsync(path + "/check", check)).EnsureSuccessStatusCode();
        Assert.Equal("design_comparison_required", (await (await validator.PostActionAsync(task.Id, "pass", vote)).ReadErrorAsync()).Code);
        (await validator.PostAsJsonAsync(path + "/check", check with { Matches = true, VisualEvidence = "All committed fixture states match." })).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(task.Id, "pass", vote)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(path + "/attach", new WriteDesignRequest(1, definition with { Summary = "Revised layout requires a fresh decision" }))).EnsureSuccessStatusCode();
        var updated = (await owner.GetFromJsonAsync(path, MuthurJsonContext.Default.DesignDto))!;
        Assert.Equal("pending", updated.ApprovalStatus);
        Assert.Equal(2, updated.Revisions.Count);
        Assert.Single(updated.Approvals);
        Assert.Equal(2, updated.Checks.Count);
        Assert.Contains("Revised layout", await hub.CreateClient().GetStringAsync("/tasks/" + task.Id + "/design"));
        Assert.False((await owner.PostActionAsync(task.Id, "pass", vote)).IsSuccessStatusCode);
    }
}
