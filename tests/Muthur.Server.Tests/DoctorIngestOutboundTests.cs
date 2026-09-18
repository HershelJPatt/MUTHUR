using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Data;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>The ingest, secret and outbound halves of the report — including what must never appear in it.</summary>
public sealed class DoctorIngestOutboundTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private async Task<DoctorDto> DoctorAsync(bool probe = false) =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe={probe}", MuthurJsonContext.Default.DoctorDto))!;

    private Task<string> DoctorBodyAsync(bool probe = false) =>
        _hub.CreateClient().GetStringAsync($"{Routes.Doctor}?probe={probe}");

    private Task<MuthurDb> DbAsync() =>
        _hub.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync();

    [Fact]
    public async Task A_source_no_adapter_can_poll_is_a_failure_that_names_the_schemes_that_exist()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);
        // A scheme this hub knew once, or a typo: project add refuses one, so it can only arrive in the database.
        await using (var db = await DbAsync())
        {
            (await db.Projects.SingleAsync()).IngestSources = ["carrier-pigeon:coop-7"];
            await db.SaveChangesAsync();
        }

        var check = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "ingest");

        Assert.Equal("carrier-pigeon:coop-7", check.Subject);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal("No ingest adapter for 'carrier-pigeon:'. Known: github, discord, fake.", check.Detail);
    }

    [Fact]
    public async Task A_source_that_has_never_polled_is_a_warning_and_one_whose_last_poll_failed_is_a_failure()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);

        var never = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "ingest");
        Assert.Equal(CheckStatus.Warn, never.Status);
        Assert.Equal("No successful poll recorded. Run: muthur inbound poll", never.Detail);
        Assert.Null(never.LastSuccess);

        _hub.Source.Add("issue/1", "Crash on start", updatedAt: "2026-01-01T10:00:00Z");
        await _hub.Services.GetRequiredService<IngestService>().PollAsync();

        var reads = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "ingest");
        Assert.Equal(CheckStatus.Ok, reads.Status);
        Assert.Equal("Reads.", reads.Detail);
        Assert.Equal(_hub.Clock.GetUtcNow(), reads.LastSuccess);

        _hub.Source.FailWith = "gh: not logged in";
        await _hub.Services.GetRequiredService<IngestService>().PollAsync();

        var failing = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "ingest");
        Assert.Equal(CheckStatus.Fail, failing.Status);
        Assert.Equal("Last poll failed: gh: not logged in", failing.Detail);
        // The successful poll is still the last one that worked: a failure loses nothing.
        Assert.Equal(_hub.Clock.GetUtcNow(), failing.LastSuccess);
    }

    [Fact]
    public async Task A_probe_that_cannot_read_the_source_says_why_in_the_sources_own_words()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);
        _hub.Source.Add("issue/1", "Crash on start", updatedAt: "2026-01-01T10:00:00Z");
        await _hub.Services.GetRequiredService<IngestService>().PollAsync();
        _hub.Source.FailWith = "the source is unreachable from here";

        // Offline, the hub only reports what it already knows: the last poll succeeded, so nothing is wrong.
        Assert.Equal(CheckStatus.Ok, Assert.Single((await DoctorAsync()).Checks, c => c.Category == "ingest").Status);

        var probed = Assert.Single((await DoctorAsync(probe: true)).Checks, c => c.Category == "ingest");
        Assert.Equal(CheckStatus.Fail, probed.Status);
        Assert.Equal("the source is unreachable from here", probed.Detail);
    }

    [Fact]
    public async Task A_discord_source_with_no_token_fails_on_the_variables_name_and_nothing_else()
    {
        _hub.Settings["Muthur:DiscordBotToken"] = ""; // whatever the machine running the tests happens to have in its environment
        await _hub.AddProjectAsync(ingest: ["discord:112233445566"]);

        var report = await DoctorAsync();

        var secret = Assert.Single(report.Checks, c => c.Category == "secret");
        Assert.Equal("Muthur__DiscordBotToken", secret.Subject);
        Assert.Equal(CheckStatus.Fail, secret.Status);
        Assert.StartsWith("Not set in this hub's environment, so discord: ingest cannot authenticate.", secret.Detail);
        Assert.Null(secret.LastSuccess);
    }

    [Fact]
    public async Task A_hub_with_no_discord_source_is_not_told_about_a_token_it_does_not_need()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);

        Assert.DoesNotContain((await DoctorAsync()).Checks, c => c.Category == "secret");
    }

    [Fact]
    public async Task A_token_that_is_set_is_reported_as_set_and_never_as_itself()
    {
        const string token = "MTIzNDU2Nzg5-this-must-never-be-printed";
        using var hub = new HubFactory();
        hub.Settings["Muthur:DiscordBotToken"] = token;
        await hub.AddProjectAsync(ingest: ["discord:112233445566"]);

        var body = await hub.CreateClient().GetStringAsync($"{Routes.Doctor}?probe=false");
        var report = await hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto);

        var secret = Assert.Single(report!.Checks, c => c.Category == "secret");
        Assert.Equal(CheckStatus.Ok, secret.Status);
        Assert.Equal("Set in this hub's environment.", secret.Detail);
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(token[..8], body, StringComparison.Ordinal); // not even a prefix of it
    }

    [Fact]
    public async Task A_target_whose_channel_no_longer_exists_is_a_failure_that_names_the_channels_that_do()
    {
        await DefineFileTargetAsync("news");
        // Only the database can hold this: defining a target checks the channel exists.
        await using (var db = await DbAsync())
        {
            (await db.OutboundTargets.SingleAsync()).Channel = "carrier-pigeon";
            await db.SaveChangesAsync();
        }

        var check = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "outbound");

        Assert.Equal("news", check.Subject);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal("No channel named 'carrier-pigeon'. Known: file, discord-webhook, github-issue.", check.Detail);
    }

    [Fact]
    public async Task A_target_whose_address_stopped_being_valid_is_a_failure_in_the_channels_own_words()
    {
        await DefineFileTargetAsync("news");
        await using (var db = await DbAsync())
        {
            (await db.OutboundTargets.SingleAsync()).Address = "outbox.txt"; // no longer absolute
            await db.SaveChangesAsync();
        }

        var check = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "outbound");

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal("A file target needs an absolute path.", check.Detail);
    }

    [Fact]
    public async Task A_probed_report_never_contains_a_targets_address()
    {
        // A file target whose path is distinctive enough that any leak of it is unmistakable.
        var address = Path.Combine(_hub.DataDir, "webhook-7f3a9c", "outbox.txt");
        await DefineFileTargetAsync("news", address, founderApproval: true);

        var report = await DoctorAsync(probe: true);
        var body = await DoctorBodyAsync(probe: true);

        var check = Assert.Single(report.Checks, c => c.Category == "outbound");
        Assert.Equal("news", check.Subject);
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal("file target, address accepted. Every message needs the founder.", check.Detail);
        Assert.All(report.Checks, c =>
        {
            Assert.DoesNotContain("webhook-7f3a9c", c.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("webhook-7f3a9c", c.Detail, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain("webhook-7f3a9c", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outbox.txt", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_target_whose_address_became_a_directory_probes_as_a_failure()
    {
        var address = Path.Combine(_hub.DataDir, "press-9c41", "outbox.txt");
        await DefineFileTargetAsync("press", address);

        // The address was a file's path when the founder allowed it; something has since created a directory there,
        // which every send to it will fail on. Doctor exists to say so before an agent finds out the hard way.
        Directory.CreateDirectory(address);

        var check = Assert.Single((await DoctorAsync(probe: true)).Checks, c => c.Category == "outbound");

        Assert.Equal("press", check.Subject);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.DoesNotContain("outbox.txt", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("press-9c41", await DoctorBodyAsync(probe: true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_file_probe_proves_the_address_itself_can_be_written_without_writing_a_byte()
    {
        // A parent directory that can be created says nothing about the address: this is what an "ok" must mean.
        var channel = new FileChannel(_hub.Clock);
        var address = Path.Combine(_hub.DataDir, "probe-9c41", "outbox.txt");

        await channel.ProbeAsync(address);
        // The probe may leave the outbox the target names — the next send would create it anyway — but never a byte
        // in it. Deleting it instead could discard a message a concurrent send appended between the two calls.
        Assert.True(File.Exists(address));
        Assert.Equal(0, new FileInfo(address).Length);

        await File.WriteAllTextAsync(address, "--- an earlier message\n");
        await channel.ProbeAsync(address);
        Assert.Equal("--- an earlier message\n", await File.ReadAllTextAsync(address));

        var directory = Path.Combine(_hub.DataDir, "probe-9c41", "a-directory");
        Directory.CreateDirectory(directory);
        var asDirectory = await Assert.ThrowsAsync<ChannelException>(() => channel.ProbeAsync(directory));
        Assert.Equal("the address is a directory, not a file", asDirectory.Message);

        // Held open exclusively: the parent exists and is writable, and the address still is not.
        var locked = Path.Combine(_hub.DataDir, "probe-9c41", "locked.txt");
        using (new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var refused = await Assert.ThrowsAsync<ChannelException>(() => channel.ProbeAsync(locked));
            Assert.Equal("the address cannot be written", refused.Message);
        }
    }

    private async Task DefineFileTargetAsync(string key, string? address = null, bool founderApproval = false) =>
        (await _hub.Founder().PutAsJsonAsync(Routes.OutboundTargets,
            new DefineTargetRequest(key, "file", address ?? Path.Combine(_hub.DataDir, "outbox.txt"), founderApproval)))
        .EnsureSuccessStatusCode();
}
