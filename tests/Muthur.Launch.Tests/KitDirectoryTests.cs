using Muthur.Launch;

namespace Muthur.Launch.Tests;

/// <summary>
/// The one answer about the kit, which both the CLI and the hub ask. MUTHUR_KIT is cleared for the life of the
/// class and restored in <see cref="Dispose"/>: the variable is process-global, and no other class in this
/// assembly reads it, so holding it here is the same bargain KitInstallTests makes in the CLI tests.
/// </summary>
public sealed class KitDirectoryTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "muthur-tests", "kitdir-" + Guid.NewGuid().ToString("n"));
    private readonly string? _previousKit = Environment.GetEnvironmentVariable(KitDirectory.Variable);

    public KitDirectoryTests()
    {
        Directory.CreateDirectory(_scratch);
        Environment.SetEnvironmentVariable(KitDirectory.Variable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KitDirectory.Variable, _previousKit);
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private string Make(string relative)
    {
        var path = Path.Combine(_scratch, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A kit directory with a manifest under each named harness — the shape install.ps1 copies.</summary>
    private string Kit(string relative, params string[] harnesses)
    {
        var kit = Make(relative);
        foreach (var harness in harnesses)
            File.WriteAllText(Path.Combine(Make(Path.Combine(relative, harness)), KitDirectory.Manifest), """{"files":[]}""");
        return kit;
    }

    [Fact]
    public void Harnesses_are_the_subdirectories_holding_a_manifest_in_ordinal_order()
    {
        var kit = Kit("shipped", "zeta", "alpha");
        Make(Path.Combine("shipped", "core"));   // procedures every harness includes, and not a harness

        var harnesses = KitDirectory.Harnesses(kit);

        Assert.NotNull(harnesses);
        Assert.Equal(["alpha", "zeta"], harnesses);
    }

    [Fact]
    public void No_directory_and_a_directory_that_is_not_there_are_both_null()
    {
        Assert.Null(KitDirectory.Harnesses(null));
        Assert.Null(KitDirectory.Harnesses(Path.Combine(_scratch, "absent")));
    }

    [Fact]
    public void An_empty_kit_directory_is_an_empty_list_and_not_null()
    {
        var harnesses = KitDirectory.Harnesses(Make("hollow"));

        // The two answers are different facts: null is "could not tell", empty is "told, and there are none".
        Assert.NotNull(harnesses);
        Assert.Empty(harnesses);
    }

    [Fact]
    public void An_explicit_setting_that_exists_is_the_answer()
    {
        var kit = Kit("configured", "claude");

        Assert.Equal(kit, KitDirectory.Locate(kit));
    }

    [Fact]
    public void An_explicit_setting_that_points_nowhere_is_not_replaced_by_a_guess()
    {
        var home = Make("cli");
        Kit(Path.Combine("cli", "kit"), "claude");

        Assert.Null(KitDirectory.Locate(Path.Combine(_scratch, "absent"), home));
    }

    [Fact]
    public void A_kit_beside_the_binary_is_found()
    {
        var home = Make("cli");
        var kit = Kit(Path.Combine("cli", "kit"), "claude");

        Assert.Equal(kit, KitDirectory.Locate(null, home));
    }

    [Fact]
    public void An_installed_hub_finds_the_kit_beside_the_server_directory_it_runs_from()
    {
        // install.ps1 publishes the server into <destination>/server/ and copies the kit to <destination>/kit,
        // so the hub's own directory has no kit/ in it and the one it wants is a level up.
        var server = Make(Path.Combine("install", "server"));
        var kit = Kit(Path.Combine("install", "kit"), "claude");

        Assert.Equal(kit, KitDirectory.Locate(null, server));
    }
}
