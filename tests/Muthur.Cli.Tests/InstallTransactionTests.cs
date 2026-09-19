using System.Text;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// The undo on its own, with no failure involved: whatever the transaction wrote, `Rollback` puts back. The
/// restores are asserted on bytes rather than on text, because `Apply` normalises line endings and a journal
/// that held a string would silently rewrite a CRLF file the repository owns.
/// </summary>
public sealed class InstallTransactionTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("muthur-undo-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Path(string name) =>
        System.IO.Path.Combine(dir, name.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private InstallTransaction New() => new(dir);

    [Fact]
    public void A_file_that_was_not_there_is_deleted()
    {
        var transaction = New();
        Assert.Equal("created", transaction.Apply(Path("x.md"), "from the kit", "replace"));

        Assert.Empty(transaction.Rollback());
        Assert.False(File.Exists(Path("x.md")));
    }

    [Theory]
    // CRLF, which a journal holding a string would have rewritten on the way back.
    [InlineData("ORIGINAL\r\nsecond line")]
    // And no trailing newline, which a restore that tidied the file would add.
    [InlineData("ORIGINAL")]
    public void A_file_that_existed_is_restored_byte_for_byte(string original)
    {
        var path = Path("owned.md");
        var before = Encoding.UTF8.GetBytes(original);
        File.WriteAllBytes(path, before);

        var transaction = New();
        Assert.Equal("updated", transaction.Apply(path, "from the kit", "replace"));
        Assert.Equal("from the kit", File.ReadAllText(path));

        Assert.Empty(transaction.Rollback());
        Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), $"{path} did not come back byte for byte.");
    }

    [Fact]
    public void The_directories_it_created_go_deepest_first_however_deep_they_are()
    {
        var transaction = New();
        transaction.Apply(Path("a/b/c/x.md"), "from the kit", "replace");
        Assert.True(Directory.Exists(Path("a/b/c")));

        Assert.Empty(transaction.Rollback());
        Assert.False(Directory.Exists(Path("a")));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public void A_directory_that_was_already_there_is_left_where_it_was()
    {
        Directory.CreateDirectory(Path("docs"));

        var transaction = New();
        transaction.Apply(Path("docs/x.md"), "from the kit", "replace");

        Assert.Empty(transaction.Rollback());
        Assert.True(Directory.Exists(Path("docs")));
        Assert.Empty(Directory.GetFileSystemEntries(Path("docs")));
    }

    /// <summary>
    /// Deleting non-recursively is what makes this safe: a directory we created but which now holds something
    /// we did not write refuses rather than taking the other file with it, and says so.
    /// </summary>
    [Fact]
    public void A_directory_holding_a_file_we_did_not_write_is_reported_rather_than_destroyed()
    {
        var transaction = New();
        transaction.Apply(Path("docs/x.md"), "from the kit", "replace");
        File.WriteAllText(Path("docs/theirs.md"), "not ours");

        Assert.Equal("docs", Assert.Single(transaction.Rollback()));

        Assert.True(Directory.Exists(Path("docs")));
        Assert.Equal("not ours", File.ReadAllText(Path("docs/theirs.md")));
        Assert.False(File.Exists(Path("docs/x.md")));
    }

    /// <summary>
    /// The restore fails for the same reason the write did, and the file is already exactly as recorded.
    /// Blaming it was the defect Amendment 1 corrects: the failure list's sentence is a claim about the file,
    /// not about the call, and this is the commonest failure there is.
    /// </summary>
    [Fact]
    public void A_destination_the_write_could_not_touch_is_not_blamed_for_being_as_recorded()
    {
        var path = Path("owned.md");
        var before = Encoding.UTF8.GetBytes("ORIGINAL\r\nsecond line");
        File.WriteAllBytes(path, before);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var transaction = New();
        try
        {
            // Journaled before the write, so the prior bytes are recorded and then the write throws.
            Assert.ThrowsAny<Exception>(() => transaction.Apply(path, "from the kit", "replace"));
            Assert.Empty(transaction.Rollback());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), $"{path} was not left as it was found.");
    }

    /// <summary>
    /// A path journaled as absent that now holds a directory nobody journaled. `File.Delete` refuses, and the
    /// directory is not ours to account for: it is not in the transaction's directory journal, and `File.Exists`
    /// on a directory is false, so the path correctly holds no file of ours.
    /// </summary>
    [Fact]
    public void A_directory_nobody_journaled_standing_where_our_file_was_is_not_blamed()
    {
        var path = Path("x.md");

        var transaction = New();
        transaction.Apply(path, "from the kit", "replace");
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.Empty(transaction.Rollback());
        Assert.True(Directory.Exists(path));
    }

    /// <summary>
    /// Two entries naming the same path are last-one-wins, not a collision, so the journal holds both and
    /// replaying it backwards ends at the state before the first of them.
    /// </summary>
    [Fact]
    public void A_path_written_twice_is_restored_to_the_state_before_the_first_write()
    {
        var path = Path("x.md");
        File.WriteAllText(path, "the repository's own");

        var transaction = New();
        transaction.Apply(path, "the first entry", "replace");
        transaction.Apply(path, "the second entry", "replace");
        Assert.Equal("the second entry", File.ReadAllText(path));

        Assert.Empty(transaction.Rollback());
        Assert.Equal("the repository's own", File.ReadAllText(path));
    }

    [Fact]
    public void Unchanged_puts_nothing_in_the_journal()
    {
        var path = Path("x.md");
        File.WriteAllText(path, "from the kit");

        var transaction = New();
        Assert.Equal("unchanged", transaction.Apply(path, "from the kit", "replace"));

        Assert.Empty(transaction.Rollback());
        Assert.Equal("from the kit", File.ReadAllText(path));
    }

    [Fact]
    public void Kept_puts_nothing_in_the_journal()
    {
        Directory.CreateDirectory(Path("briefs"));
        var brief = Path("briefs/validator.md");
        File.WriteAllText(brief, "the founder's own words");

        var transaction = New();
        Assert.Equal("kept", transaction.Apply(brief, "starter", "create"));

        Assert.Empty(transaction.Rollback());
        Assert.Equal("the founder's own words", File.ReadAllText(brief));
        Assert.True(Directory.Exists(Path("briefs")));
    }

    [Fact]
    public void Commit_forgets_the_journal()
    {
        var transaction = New();
        transaction.Apply(Path("docs/x.md"), "from the kit", "replace");
        transaction.Commit();

        Assert.Empty(transaction.Rollback());
        Assert.Equal("from the kit", File.ReadAllText(Path("docs/x.md")));
        Assert.True(Directory.Exists(Path("docs")));
    }

    /// <summary>
    /// `Write` is what `.gitignore` and the project file use, and the reason they do not go through `Apply`:
    /// the bytes go down as given, so an append to a file the repository wrote with CRLF stays CRLF.
    /// </summary>
    [Fact]
    public void Write_puts_the_bytes_down_exactly_as_it_was_given_them()
    {
        const string verbatim = "bin/\r\nobj/\r\n.worktrees/\n";

        var transaction = New();
        transaction.Write(Path(".gitignore"), verbatim);
        Assert.True(
            Encoding.UTF8.GetBytes(verbatim).SequenceEqual(File.ReadAllBytes(Path(".gitignore"))),
            "Write normalised the bytes it was given; .gitignore's CRLF would not survive an install.");

        Assert.Empty(transaction.Rollback());
        Assert.False(File.Exists(Path(".gitignore")));
    }

    [Fact]
    public void Touching_names_the_path_the_install_last_read_or_wrote()
    {
        var transaction = New();
        Assert.Null(transaction.Touching);

        transaction.Apply(Path("a.md"), "from the kit", "replace");
        Assert.Equal(Path("a.md"), transaction.Touching);

        // Set on the way in rather than on the way out, so a mode that writes nothing still names the path a
        // failure would be about.
        transaction.Apply(Path("b.md"), "from the kit", "replace");
        Assert.Equal(Path("b.md"), transaction.Touching);
        Assert.Equal("unchanged", transaction.Apply(Path("a.md"), "from the kit", "replace"));
        Assert.Equal(Path("a.md"), transaction.Touching);

        transaction.Write(Path(".gitignore"), ".worktrees/\n");
        Assert.Equal(Path(".gitignore"), transaction.Touching);

        transaction.Rollback();
    }
}
