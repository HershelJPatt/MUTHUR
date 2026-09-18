using System.Net.Http.Json;
using Muthur.Core;
using Muthur.Launch;

namespace Muthur.Server.Services;

/// <summary>Appends to a local file. The "outbox" for dry runs, and the channel other local tools can watch.</summary>
public sealed class FileChannel(TimeProvider clock) : IOutboundChannel
{
    public string Name => "file";

    public void Validate(string address, string body)
    {
        if (!Path.IsPathRooted(address))
            throw Fail.Rule("invalid_address", "A file target needs an absolute path.");
    }

    public async Task SendAsync(string address, string body, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(address)!);
        // The body goes out byte for byte: it is what was hashed and reviewed.
        await File.AppendAllTextAsync(address, $"--- {clock.GetUtcNow():O}\n{body}\n", ct);
    }

    public Task ProbeAsync(string address, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(address)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ChannelException("the directory cannot be created or written");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Posts to a Discord channel through a webhook URL (the target's address, a credential the agents never see).</summary>
public sealed class DiscordWebhookChannel(IHttpClientFactory http) : IOutboundChannel
{
    private const int MaxLength = 2000;

    public string Name => "discord-webhook";

    public void Validate(string address, string body)
    {
        if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw Fail.Rule("invalid_address", "A Discord target needs its https webhook URL.");
        if (body.Length > MaxLength)
            throw Fail.Rule("body_too_long", $"Discord messages are limited to {MaxLength} characters; this one has {body.Length}.");
    }

    public async Task SendAsync(string address, string body, CancellationToken ct = default)
    {
        using var client = http.CreateClient(nameof(DiscordWebhookChannel));
        // allowed_mentions: an agent's text must never be able to ping @everyone.
        using var response = await client.PostAsJsonAsync(address, new { content = body, allowed_mentions = new { parse = Array.Empty<string>() } }, ct);
        if (!response.IsSuccessStatusCode)
            throw new ChannelException($"Discord answered HTTP {(int)response.StatusCode}");
    }

    public async Task ProbeAsync(string address, CancellationToken ct = default)
    {
        using var client = http.CreateClient(nameof(DiscordWebhookChannel));
        // A webhook URL answers a GET with the webhook's own metadata and posts nothing.
        using var response = await client.GetAsync(address, ct);
        if (!response.IsSuccessStatusCode)
            throw new ChannelException($"Discord answered HTTP {(int)response.StatusCode}");
    }
}

/// <summary>Comments on a GitHub issue or pull request. Address: "owner/repo#123". Uses the GitHub CLI's authentication.</summary>
public sealed class GitHubIssueChannel(IProcessRunner processes) : IOutboundChannel
{
    public string Name => "github-issue";

    public void Validate(string address, string body) => Parse(address);

    public async Task SendAsync(string address, string body, CancellationToken ct = default)
    {
        var (repo, number) = Parse(address);
        var result = await processes.RunAsync("gh", ["api", "--method", "POST", $"repos/{repo}/issues/{number}/comments", "--input", "-"],
            Environment.CurrentDirectory, stdin: System.Text.Json.JsonSerializer.Serialize(new { body }), timeout: TimeSpan.FromSeconds(60), ct: ct);
        if (!result.Ok) throw new ChannelException($"the GitHub CLI failed (exit {result.ExitCode}); is it logged in? gh auth status");
    }

    public async Task ProbeAsync(string address, CancellationToken ct = default)
    {
        var (repo, number) = Parse(address);
        // Reading the issue proves both that gh is logged in and that the issue is still there to comment on.
        var result = await processes.RunAsync("gh", ["api", $"repos/{repo}/issues/{number}"],
            Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(60), ct: ct);
        if (!result.Ok) throw new ChannelException($"the GitHub CLI failed (exit {result.ExitCode}); is it logged in? gh auth status");
    }

    private static (string Repo, int Number) Parse(string address)
    {
        var hash = address.LastIndexOf('#');
        if (hash <= 0 || !address[..hash].Contains('/') || !int.TryParse(address[(hash + 1)..], out var number))
            throw Fail.Rule("invalid_address", "A github-issue target's address is owner/repo#<number>.");
        return (address[..hash], number);
    }
}
