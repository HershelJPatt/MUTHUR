using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// A hub that has served a request holds files inside its data directory open. These assert that disposing it
/// lets go of all of them — a handle that outlives the hub leaves the directory behind, which is how two of
/// them went unnoticed for so long.
/// </summary>
public sealed class HubDisposalTests
{
    private const string BackupsDirectory = "backups";

    [Fact]
    public async Task A_disposed_hub_releases_its_log()
    {
        var hub = new HubFactory();
        var dataDir = hub.DataDir;
        try
        {
            (await hub.Founder().GetAsync(Routes.Status)).EnsureSuccessStatusCode();
            Assert.True(File.Exists(Path.Combine(dataDir, MuthurEnvironment.LogFile)),
                "the hub should have written a log, or there is no handle under test");
        }
        finally
        {
            hub.Dispose();
        }

        Assert.False(Directory.Exists(dataDir),
            $"{dataDir} survived disposal: the hub is still holding a file inside it open");
    }

    [Fact]
    public async Task A_disposed_hub_releases_the_backup_it_wrote()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "muthur-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dataDir);
        HubTestExtensions.SeedUnmigratedDatabase(Path.Combine(dataDir, MuthurEnvironment.DatabaseFile));

        var hub = new HubFactory { DataDir = dataDir };
        try
        {
            (await hub.Founder().GetAsync(Routes.Status)).EnsureSuccessStatusCode();
            Assert.Single(Directory.GetFiles(Path.Combine(dataDir, BackupsDirectory)));
        }
        finally
        {
            hub.Dispose();
        }

        Assert.False(Directory.Exists(dataDir),
            $"{dataDir} survived disposal: the copy taken before migrating is still open");
    }
}
