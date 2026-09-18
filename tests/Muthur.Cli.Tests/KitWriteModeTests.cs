using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// The write modes behind `muthur kit install`. `create` is the one that matters: a brief stops being
/// MUTHUR's the moment the founder edits it, and the install is advertised as safe to re-run.
/// </summary>
public sealed class KitWriteModeTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("muthur-kit-").FullName;

    private string Path(string name) => System.IO.Path.Combine(dir, name);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Replace_writes_a_file_that_was_not_there()
    {
        Assert.Equal("created", KitCommands.WriteKitFile(Path("procedure.md"), "one", "replace"));
        Assert.Equal("one", File.ReadAllText(Path("procedure.md")));
    }

    [Fact]
    public void Replace_overwrites_what_is_there()
    {
        KitCommands.WriteKitFile(Path("procedure.md"), "one", "replace");
        Assert.Equal("updated", KitCommands.WriteKitFile(Path("procedure.md"), "two", "replace"));
        Assert.Equal("two", File.ReadAllText(Path("procedure.md")));
    }

    [Fact]
    public void Replace_says_so_when_the_bytes_already_match()
    {
        KitCommands.WriteKitFile(Path("procedure.md"), "one", "replace");
        Assert.Equal("unchanged", KitCommands.WriteKitFile(Path("procedure.md"), "one", "replace"));
    }

    [Fact]
    public void Create_writes_a_brief_that_was_not_there()
    {
        Assert.Equal("created", KitCommands.WriteKitFile(Path("briefs/validator.md"), "starter", "create"));
        Assert.Equal("starter", File.ReadAllText(Path("briefs/validator.md")));
    }

    [Fact]
    public void Create_keeps_the_founders_edit_byte_for_byte()
    {
        var brief = Path("briefs/validator.md");
        KitCommands.WriteKitFile(brief, "starter", "create");
        File.WriteAllText(brief, "the founder's own words");

        Assert.Equal("kept", KitCommands.WriteKitFile(brief, "starter", "create"));
        Assert.Equal("the founder's own words", File.ReadAllText(brief));
    }

    [Fact]
    public void An_unknown_mode_replaces_like_the_default()
    {
        KitCommands.WriteKitFile(Path("procedure.md"), "one", null);
        Assert.Equal("updated", KitCommands.WriteKitFile(Path("procedure.md"), "two", null));
    }
}
