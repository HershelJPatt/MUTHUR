using System.Net.Http.Json;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>Whether doctor can tell the founder that the hub's own log is writable — without disturbing it.</summary>
public sealed class DoctorLoggingTests : IDisposable
{
    private static readonly DoctorContext Context = new(Probe: false, Now: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private readonly HubFactory _hub = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));

    public DoctorLoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string LogPath => Path.Combine(_dir, MuthurEnvironment.LogFile);

    private async Task<CheckDto> DirectAsync() =>
        Assert.Single(await new DoctorLoggingCheck(new MuthurOptions { DataDir = _dir }).RunAsync(Context));

    private async Task<DoctorDto> DoctorAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto))!;

    [Fact]
    public async Task A_healthy_hub_says_which_log_it_is_writing()
    {
        var check = Assert.Single((await DoctorAsync()).Checks, c => c.Category == "logging");

        Assert.Equal(MuthurEnvironment.LogFile, check.Subject);
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal($"Writing to {Path.Combine(_hub.DataDir, MuthurEnvironment.LogFile)}.", check.Detail);
    }

    [Fact]
    public async Task A_log_path_that_is_a_directory_is_a_failure()
    {
        // A directory passes every check the hub makes until the write itself, which fails with access denied.
        Directory.CreateDirectory(LogPath);

        var check = await DirectAsync();

        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Equal($"{LogPath} is a directory; the hub cannot write its log there.", check.Detail);
    }

    [Fact]
    public async Task The_probe_leaves_what_the_hub_already_logged_exactly_where_it_was()
    {
        await File.WriteAllTextAsync(LogPath, "hub started, and this line is the record" + Environment.NewLine);
        var written = await File.ReadAllBytesAsync(LogPath);

        Assert.Equal(CheckStatus.Ok, (await DirectAsync()).Status);

        // T-14's lesson: a probe that deletes what it created can discard what a concurrent writer appended,
        // and here the concurrent writer is the hub itself.
        Assert.True(File.Exists(LogPath), "the probe deleted the hub's log");
        Assert.Equal(written, await File.ReadAllBytesAsync(LogPath));
    }

    [Fact]
    public async Task The_report_reads_the_hubs_own_log_before_anything_that_depends_on_it()
    {
        await _hub.AddProjectAsync(ingest: ["fake:acme/widgets"]);

        var categories = (await DoctorAsync()).Checks.Select(c => c.Category).ToList();

        Assert.Contains("logging", categories);
        Assert.Contains("ingest", categories);
        Assert.True(categories.IndexOf("logging") < categories.IndexOf("ingest"),
            $"logging must be read before ingest, but the report ordered them {string.Join(", ", categories)}");
    }
}
