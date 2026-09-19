using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>
/// Whether every configured source can still be read, and whether the secret such a source needs is even set.
/// A secret is reported as present or absent and never in any other way: no value, no prefix, no length.
/// </summary>
public sealed class DoctorIngestCheck(Ledger ledger, IEnumerable<IInboundSource> sources, MuthurOptions options) : IDoctorCheck
{
    private const string DiscordScheme = "discord";
    private const string DiscordTokenVariable = "Muthur__DiscordBotToken";

    public async Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
    {
        var (configured, cursors) = await ledger.ReadAsync(async (db, _) =>
        {
            var projects = await db.Projects.ToListAsync(ct);
            var rows = await db.IngestCursors.ToDictionaryAsync(c => c.Source, ct);
            // Two projects may poll the same source; it has one cursor, so it gets one line.
            var distinct = projects.SelectMany(p => p.IngestSources).Distinct(StringComparer.Ordinal).ToList();
            return (distinct, rows);
        }, ct);

        var parsed = configured.Select(s => (Source: s, Parts: Split(s))).ToList();
        var checks = new List<CheckDto>();
        // Only a secret some configured source actually needs is worth a line.
        if (parsed.Any(p => p.Parts?.Scheme == DiscordScheme)) checks.Add(DiscordToken());
        foreach (var (source, parts) in parsed)
            checks.Add(await SourceAsync(source, parts, cursors.GetValueOrDefault(source), context, ct));
        return checks;
    }

    private CheckDto DiscordToken() =>
        options.DiscordBotToken is { Length: > 0 }
            ? new CheckDto("secret", DiscordTokenVariable, CheckStatus.Ok, "Set in this hub's environment.")
            : new CheckDto("secret", DiscordTokenVariable, CheckStatus.Fail,
                "Not set in this hub's environment, so discord: ingest cannot authenticate. Set Muthur__DiscordBotToken and restart the hub — " +
                "a hub started from a shell opened before the variable was set does not see it.");

    private async Task<CheckDto> SourceAsync(string source, (string Scheme, string Location)? parts, IngestCursor? cursor,
        DoctorContext context, CancellationToken ct)
    {
        // Whatever the verdict, "when did this last work" is the first thing anyone reading the line wants.
        CheckDto Result(CheckStatus status, string detail) => new("ingest", source, status, detail, cursor?.LastSuccessAt);

        if (parts is not { } shape)
            return Result(CheckStatus.Fail, "Not a source: expected scheme:location, e.g. github:owner/repo.");

        var adapter = sources.FirstOrDefault(s => s.Scheme == shape.Scheme);
        if (adapter is null)
            return Result(CheckStatus.Fail, $"No ingest adapter for '{shape.Scheme}:'. Known: {string.Join(", ", sources.Select(s => s.Scheme))}.");
        if (cursor?.LastError is { } error)
            return Result(CheckStatus.Fail, $"Last poll failed: {error}");
        if (cursor?.LastSuccessAt is null)
            return Result(CheckStatus.Warn, "No successful poll recorded. Run: muthur inbound poll");

        if (context.Probe)
        {
            try
            {
                await adapter.ProbeAsync(shape.Location, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A probe's message is written to be shown to the founder; it names the source, never a credential.
                return Result(CheckStatus.Fail, ex.Message);
            }
        }
        return Result(CheckStatus.Ok, "Reads.");
    }

    private static (string Scheme, string Location)? Split(string source)
    {
        var colon = source.IndexOf(':');
        return colon <= 0 || colon == source.Length - 1 ? null : (source[..colon], source[(colon + 1)..]);
    }
}
