using Microsoft.Extensions.DependencyInjection;
using Muthur.Launch;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// Every conductor-started session leaves a bill. A session that hit its timeout or a quota is the spend receipts
/// exist to show, so the row is written for every attempt, not only the productive ones.
/// </summary>
public sealed class SessionReceiptsTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    [Fact]
    public async Task Every_attempt_is_recorded_with_its_cost_and_failure_kind()
    {
        await _hub.AddProjectAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Priced work");
        var ledger = _hub.Services.GetRequiredService<Ledger>();
        var taskId = int.Parse(task.Id.AsSpan(2));

        await SessionReceipts.RecordAsync(ledger, taskId, "#orchestrator",
        [
            new(new("codex", "gpt", "chatgpt"), new(false, "usage limit", RateLimited: true, TotalTokens: 66882), TimeSpan.FromSeconds(4), FailureKind: "quota", ExitCode: 1, RunId: "a"),
            new(new("claude", "opus", "claude-sub"), new(true, "STATUS: done", false, CostUsd: 0.31m, InputTokens: 1200, OutputTokens: 340, CacheReadTokens: 9000), TimeSpan.FromSeconds(34), RunId: "b", PromptBytes: 6144),
        ], default);

        var events = (await _hub.Founder().GetTaskAsync(task.Id)).Events.Where(e => e.Type == "conductor.session_finished").ToList();
        Assert.Equal(2, events.Count);
        Assert.Contains("\"harness\":\"codex\"", events[0].Payload.GetRawText());
        Assert.Contains("\"failureKind\":\"quota\"", events[0].Payload.GetRawText());
        Assert.Contains("\"totalTokens\":66882", events[0].Payload.GetRawText());
        Assert.Contains("\"costUsd\":null", events[0].Payload.GetRawText());
        Assert.Contains("\"harness\":\"claude\"", events[1].Payload.GetRawText());
        Assert.Contains("\"costUsd\":0.31", events[1].Payload.GetRawText());
        Assert.Contains("\"inputTokens\":1200", events[1].Payload.GetRawText());
        Assert.Contains("\"cacheReadTokens\":9000", events[1].Payload.GetRawText());
        Assert.Contains("\"promptBytes\":6144", events[1].Payload.GetRawText());
        Assert.Contains("\"seconds\":34", events[1].Payload.GetRawText());
        Assert.Contains("\"role\":\"#orchestrator\"", events[1].Payload.GetRawText());
    }

    [Fact]
    public void A_timeout_without_notes_is_named_as_the_restart_it_is()
    {
        WorkerAttempt timedOut = new(new("claude", "opus", "a"), new(false, "", false), TimeSpan.FromMinutes(45), FailureKind: "timeout", ExitCode: 124);
        WorkerAttempt quota = new(new("codex", "gpt", "b"), new(false, "", true), TimeSpan.FromSeconds(3), FailureKind: "quota");

        Assert.Equal(["timeout_without_notes", "quota"], SessionReceipts.NotesAware([timedOut, quota], hasNotes: false).Select(a => a.FailureKind));
        Assert.Equal(["timeout", "quota"], SessionReceipts.NotesAware([timedOut, quota], hasNotes: true).Select(a => a.FailureKind));
    }

    [Fact]
    public async Task Nothing_is_recorded_when_no_candidate_was_tried()
    {
        await _hub.AddProjectAsync();
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Unstaffed");
        await SessionReceipts.RecordAsync(_hub.Services.GetRequiredService<Ledger>(), int.Parse(task.Id.AsSpan(2)), "win-validator", [], default);
        Assert.DoesNotContain((await _hub.Founder().GetTaskAsync(task.Id)).Events, e => e.Type == "conductor.session_finished");
    }
}
