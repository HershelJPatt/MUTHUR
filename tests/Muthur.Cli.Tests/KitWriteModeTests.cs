using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// The write modes behind `muthur kit install`. `create` is the one that matters: a brief stops being
/// MUTHUR's the moment the founder edits it, and the install is advertised as safe to re-run.
/// </summary>
public sealed class KitWriteModeTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("muthur-kit-").FullName;

    /// <summary>The modes are the transaction's now. Nothing here rolls back, so one per test is enough.</summary>
    private readonly InstallTransaction transaction;

    public KitWriteModeTests() => transaction = new InstallTransaction(dir);

    private string Path(string name) => System.IO.Path.Combine(dir, name);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Replace_writes_a_file_that_was_not_there()
    {
        Assert.Equal("created", transaction.Apply(Path("procedure.md"), "one", "replace"));
        Assert.Equal("one", File.ReadAllText(Path("procedure.md")));
    }

    [Fact]
    public void Replace_overwrites_what_is_there()
    {
        transaction.Apply(Path("procedure.md"), "one", "replace");
        Assert.Equal("updated", transaction.Apply(Path("procedure.md"), "two", "replace"));
        Assert.Equal("two", File.ReadAllText(Path("procedure.md")));
    }

    [Fact]
    public void Replace_says_so_when_the_bytes_already_match()
    {
        transaction.Apply(Path("procedure.md"), "one", "replace");
        Assert.Equal("unchanged", transaction.Apply(Path("procedure.md"), "one", "replace"));
    }

    [Fact]
    public void Create_writes_a_brief_that_was_not_there()
    {
        Assert.Equal("created", transaction.Apply(Path("briefs/validator.md"), "starter", "create"));
        Assert.Equal("starter", File.ReadAllText(Path("briefs/validator.md")));
    }

    [Fact]
    public void Create_keeps_the_founders_edit_byte_for_byte()
    {
        var brief = Path("briefs/validator.md");
        transaction.Apply(brief, "starter", "create");
        File.WriteAllText(brief, "the founder's own words");

        Assert.Equal("kept", transaction.Apply(brief, "starter", "create"));
        Assert.Equal("the founder's own words", File.ReadAllText(brief));
    }

    [Fact]
    public void An_unknown_mode_replaces_like_the_default()
    {
        transaction.Apply(Path("procedure.md"), "one", null);
        Assert.Equal("updated", transaction.Apply(Path("procedure.md"), "two", null));
    }
}

/// <summary>
/// A brief's filename is its role key, and the hub only infers the validator flag from a '-validator'
/// suffix. A starter brief named `validator` therefore has to ask for the flag explicitly.
/// </summary>
public sealed class KitRoleCommandTests
{
    [Theory]
    [InlineData("briefs/validator.md", true, "muthur role define validator --brief-file briefs/validator.md --validator --founder")]
    [InlineData("briefs/web-validator.md", true, "muthur role define web-validator --brief-file briefs/web-validator.md --founder")]
    [InlineData("briefs/comms-oncall.md", false, "muthur role define comms-oncall --brief-file briefs/comms-oncall.md --founder")]
    public void The_printed_command_creates_the_role_the_brief_describes(string brief, bool validator, string expected) =>
        Assert.Equal(expected, KitCommands.RoleDefineCommand(brief, validator));
}
