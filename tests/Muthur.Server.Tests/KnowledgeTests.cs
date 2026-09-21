using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class KnowledgeTests : IDisposable
{
    private readonly HubFactory hub = new();
    public void Dispose() => hub.Dispose();

    [Fact]
    public async Task Publication_detects_drift_and_edits_retire_old_context_without_erasing_history()
    {
        await hub.AddProjectAsync();
        var writer = await hub.RegisterAgentAsync("knowledge-owner");
        var definition = new LessonDefinition("Windows fixture sharing violations", "observed_fact", "A Git fixture can remain locked briefly after an owned command exits.",
            "T-123 cleanup regression evidence", null, "demo", "validator", "codex", "windows", "fixture-config", "fixture cleanup",
            "RepositoryFixtureCleanupTests", "Review when process containment or cleanup policy changes.");
        var response = await writer.PostAsJsonAsync("/api/v1/knowledge", new LessonWriteRequest(definition));
        response.EnsureSuccessStatusCode();
        var lesson = (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.LessonDto))!;
        var path = "/api/v1/knowledge/" + lesson.Id;
        var hash = lesson.Revisions[0].Sha256;
        var publish = new LessonPublishRequest(1, hash, hash, "Deterministic cleanup regressions checked.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await writer.PostAsJsonAsync(path + "/publish", publish)).StatusCode);
        Assert.Equal("knowledge_drift", (await (await hub.Founder().PostAsJsonAsync(path + "/publish", publish with { InstalledSha256 = "old" })).ReadErrorAsync()).Code);
        (await hub.Founder().PostAsJsonAsync(path + "/publish", publish)).EnsureSuccessStatusCode();
        var service = hub.Services.GetRequiredService<KnowledgeService>();
        var context = await service.ContextAsync("demo", "validator", "codex", "windows", "fixture-config");
        Assert.Equal(hash, Assert.Single(context.Lessons).Sha256);
        Assert.Contains("do not grant permissions", context.Text);
        Assert.Empty((await service.ContextAsync("demo", "validator", "claude", "windows", "fixture-config")).Lessons);
        Assert.Empty((await service.ContextAsync("demo", "validator", "codex", "linux", "fixture-config")).Lessons);
        Assert.Empty((await service.ContextAsync("demo", "validator", "codex", "windows", "changed")).Lessons);
        (await writer.PostAsJsonAsync(path + "/edit", new LessonWriteRequest(definition with { Summary = "Revised explanation pending review." }, 1))).EnsureSuccessStatusCode();
        Assert.Empty((await service.ContextAsync("demo", "validator", "codex", "windows", "fixture-config")).Lessons);
        var updated = await service.GetAsync(lesson.Id);
        Assert.Equal(2, updated.Revisions.Count);
        Assert.Single(updated.Publications);
        Assert.Equal(HttpStatusCode.Conflict, (await hub.Founder().PostAsJsonAsync(path + "/publish", publish)).StatusCode);
        (await hub.Founder().PostAsJsonAsync(path + "/retire", new LessonRetireRequest(2, "Superseded by process containment policy."))).EnsureSuccessStatusCode();
        Assert.Equal("retired", (await service.GetAsync(lesson.Id)).State);
        Assert.Equal(2, (await service.GetAsync(lesson.Id)).Revisions.Count);
    }
}
