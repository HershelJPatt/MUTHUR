using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed record SourceFetch(IReadOnlyList<IncomingItem> Items, string? Cursor);

/// <summary>One kind of external source. A project's source is written "scheme:location", e.g. "github:owner/repo".</summary>
public interface IInboundSource
{
    string Scheme { get; }

    /// <summary>Items at or after <paramref name="cursor"/>, and the cursor to resume from. Throws <see cref="InvalidOperationException"/> when the source cannot be read.</summary>
    Task<SourceFetch> FetchAsync(string location, string? cursor, CancellationToken ct = default);
}

/// <summary>
/// Pull-based ingest. The hub is a local process that is off for hours at a time, so it never relies on being
/// told about anything (webhooks): it asks each source "what is new since my cursor" and catches up on start.
/// </summary>
public sealed class IngestService(Ledger ledger, IEnumerable<IInboundSource> sources, ILogger<IngestService> logger)
{
    private readonly SemaphoreSlim _polling = new(1, 1);

    public async Task<PollResultDto> PollAsync(CancellationToken ct = default)
    {
        await _polling.WaitAsync(ct);
        try
        {
            var targets = await ledger.ReadAsync(async (db, _) =>
            {
                var projects = await db.Projects.ToListAsync(ct);
                var cursors = await db.IngestCursors.ToDictionaryAsync(c => c.Source, c => c.Cursor, ct);
                return projects.SelectMany(p => p.IngestSources.Select(s => (Project: p, Source: s, Cursor: cursors.GetValueOrDefault(s)))).ToList();
            }, ct);

            var newItems = 0;
            var errors = new List<string>();
            foreach (var (project, source, cursor) in targets)
            {
                try
                {
                    var (scheme, location) = Split(source);
                    var adapter = sources.FirstOrDefault(s => s.Scheme == scheme)
                        ?? throw new InvalidOperationException($"No ingest adapter for '{scheme}:'. Known: {string.Join(", ", sources.Select(s => s.Scheme))}.");
                    var fetched = await adapter.FetchAsync(location, cursor, ct); // network, outside the write lock
                    newItems += await ledger.MutateAsync(Caller.System, async m =>
                    {
                        var created = await InboundService.StoreAsync(m, source, project.Id, fetched.Items, ct);
                        await SaveCursorAsync(m, source, fetched.Cursor ?? cursor, error: null, ct);
                        return created.Count;
                    }, ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                {
                    errors.Add($"{source}: {ex.Message}");
                    logger.LogWarning("Ingest of {Source} failed: {Message}", source, ex.Message);
                    await ledger.MutateAsync(Caller.System, m => SaveCursorAsync(m, source, cursor, ex.Message, ct), ct);
                }
            }
            return new PollResultDto(targets.Count, newItems, errors);
        }
        finally
        {
            _polling.Release();
        }
    }

    public Task<IReadOnlyList<IngestSourceDto>> SourcesAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<IngestSourceDto>>(async (db, _) =>
        {
            var projects = await db.Projects.OrderBy(p => p.Key).ToListAsync(ct);
            var cursors = await db.IngestCursors.ToDictionaryAsync(c => c.Source, ct);
            return projects.SelectMany(p => p.IngestSources.Select(s =>
            {
                var c = cursors.GetValueOrDefault(s);
                return new IngestSourceDto(s, p.Key, c?.Cursor, c?.UpdatedAt, c?.LastError);
            })).ToList();
        }, ct);

    /// <summary>The cursor moves only on success; a failure is remembered (and recorded once, when it starts) but loses nothing.</summary>
    private static async Task SaveCursorAsync(Mutation m, string source, string? cursor, string? error, CancellationToken ct)
    {
        var row = await m.Db.IngestCursors.SingleOrDefaultAsync(c => c.Source == source, ct);
        if (row is null)
        {
            row = new IngestCursor { Source = source };
            m.Db.IngestCursors.Add(row);
        }
        if (error is not null && row.LastError is null) m.Record("ingest.failing", payload: new { source, error });
        if (error is null && row.LastError is not null) m.Record("ingest.recovered", payload: new { source });
        row.Cursor = cursor;
        row.LastError = error;
        row.UpdatedAt = m.Now;
        if (error is null) row.LastSuccessAt = m.Now;
    }

    private static (string Scheme, string Location) Split(string source)
    {
        var colon = source.IndexOf(':');
        if (colon <= 0 || colon == source.Length - 1)
            throw new InvalidOperationException($"'{source}' is not a source; expected scheme:location, e.g. github:owner/repo.");
        return (source[..colon], source[(colon + 1)..]);
    }
}

/// <summary>Open GitHub issues through the GitHub CLI, which already holds the user's authentication.</summary>
public sealed class GitHubIssuesSource(IProcessRunner processes) : IInboundSource
{
    public string Scheme => "github";

    public async Task<SourceFetch> FetchAsync(string location, string? cursor, CancellationToken ct = default)
    {
        var arguments = new List<string>
        {
            "api", "--method", "GET", $"repos/{location}/issues",
            "-f", "state=open", "-f", "sort=updated", "-f", "direction=asc", "-f", "per_page=100",
        };
        if (cursor is { Length: > 0 })
        {
            arguments.Add("-f");
            arguments.Add($"since={cursor}");
        }

        var result = await processes.RunAsync("gh", arguments, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(60), ct: ct);
        if (!result.Ok) throw new InvalidOperationException($"gh api failed: {result.Message}");
        return Parse(result.StdOut, cursor);
    }

    public static SourceFetch Parse(string json, string? cursor)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<IncomingItem>();
        var newest = cursor;
        foreach (var issue in doc.RootElement.EnumerateArray())
        {
            if (issue.TryGetProperty("pull_request", out _)) continue; // the issues API also lists pull requests
            var updated = issue.GetProperty("updated_at").GetString();
            if (string.CompareOrdinal(updated, newest) > 0) newest = updated; // ISO-8601 UTC sorts as text

            items.Add(new IncomingItem(
                $"issue/{issue.GetProperty("number").GetInt32()}",
                issue.GetProperty("title").GetString() ?? "(untitled)",
                issue.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "",
                issue.TryGetProperty("html_url", out var url) ? url.GetString() : null,
                issue.TryGetProperty("user", out var user) && user.TryGetProperty("login", out var login) ? login.GetString() : null));
        }
        return new SourceFetch(items, newest);
    }
}

/// <summary>
/// Messages a person typed in one Discord channel. Polled with the newest message id as the cursor, because the
/// hub is off for hours at a time and a webhook delivered to a process that is not running is simply lost.
/// </summary>
public sealed class DiscordChannelSource(IHttpClientFactory http, MuthurOptions options) : IInboundSource
{
    private const int TitleLength = 120;

    public string Scheme => "discord";

    public async Task<SourceFetch> FetchAsync(string location, string? cursor, CancellationToken ct = default)
    {
        if (options.DiscordBotToken is not { Length: > 0 } token)
            throw new InvalidOperationException("No Discord bot token. Set the environment variable Muthur__DiscordBotToken and restart the hub.");

        var (_, channel) = SplitLocation(location);
        var url = $"https://discord.com/api/v10/channels/{channel}/messages?limit=100";
        if (cursor is { Length: > 0 }) url += $"&after={cursor}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // "Bot <token>" is Discord's scheme for a bot account. The token never leaves this line.
        request.Headers.TryAddWithoutValidation("Authorization", $"Bot {token}");
        using var response = await http.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord returned {(int)response.StatusCode} for channel {channel}.");

        return Parse(await response.Content.ReadAsStringAsync(ct), cursor, location);
    }

    /// <summary>A channel id, or "guild/channel" — the guild is only needed to build a link back to the message.</summary>
    internal static (string? Guild, string Channel) SplitLocation(string location)
    {
        var slash = location.IndexOf('/');
        return slash < 0 ? (null, location) : (location[..slash], location[(slash + 1)..]);
    }

    public static SourceFetch Parse(string json, string? cursor, string location)
    {
        var (guild, channel) = SplitLocation(location);
        using var doc = JsonDocument.Parse(json);
        var newest = cursor;
        var items = new List<(ulong Id, IncomingItem Item)>();

        foreach (var message in doc.RootElement.EnumerateArray())
        {
            var id = message.GetProperty("id").GetString()!;
            // Snowflakes are ids, not text: compared as text, a 19-digit id sorts below an 18-digit one.
            // The cursor advances past every message we saw, including the ones we skip below - otherwise our
            // own latest reply is never passed, and every poll fetches that same page again.
            if (ulong.TryParse(id, out var snowflake) &&
                (newest is null || (ulong.TryParse(newest, out var high) && snowflake > high)))
                newest = id;

            // Our own replies arrive through the webhook as bot messages. Ingesting them would make the
            // comms on-call answer itself, forever. Only what a person typed becomes inbound.
            if (message.TryGetProperty("author", out var author) && author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True)
                continue;
            if (message.TryGetProperty("webhook_id", out var hook) && hook.ValueKind is not JsonValueKind.Null)
                continue;

            var content = (message.TryGetProperty("content", out var c) ? c.GetString() : null)?.Trim();
            if (string.IsNullOrEmpty(content)) continue;   // an attachment or a sticker alone: nothing to act on

            items.Add((snowflake, new IncomingItem(
                id,
                Title(content),
                content,
                guild is null ? null : $"https://discord.com/channels/{guild}/{channel}/{id}",
                author.ValueKind == JsonValueKind.Object && author.TryGetProperty("username", out var name) ? name.GetString() : null)));
        }

        // Discord answers newest first; the organization reads them in the order they were written.
        return new SourceFetch(items.OrderBy(i => i.Id).Select(i => i.Item).ToList(), newest);
    }

    private static string Title(string content)
    {
        var line = content.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)].TrimEnd() + "…";
    }
}

public sealed class IngestWorker(IServiceProvider services, MuthurOptions options, TimeProvider clock, ILogger<IngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first pass runs at once: it is the catch-up for whatever happened while the hub was off.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await services.GetRequiredService<IngestService>().PollAsync(stoppingToken);
                if (result.NewItems > 0) logger.LogInformation("Ingested {Count} new inbound item(s).", result.NewItems);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ingest pass failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, options.IngestIntervalSeconds)), clock, stoppingToken);
        }
    }
}
