using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur task notes` decides before the hub is called whether it was given notes: on the wire, blank notes are
/// the clear, so a forgotten --file would silently erase what the last session left instead of being refused.
/// </summary>
public sealed class TaskNotesCommandTests
{
    [Fact]
    public void The_command_takes_a_file_inline_text_or_clear()
    {
        var root = new RootCommand("test");
        TaskCommands.AddTo(root);

        Assert.Empty(root.Parse(["task", "notes", "T-3", "--file", "notes.md"]).Errors);
        Assert.Empty(root.Parse(["task", "notes", "T-3", "--notes", "the export is CSV"]).Errors);
        Assert.Empty(root.Parse(["task", "notes", "T-3", "--clear"]).Errors);
    }

    [Fact]
    public void With_nothing_it_says_what_to_pass()
    {
        var refusal = TaskCommands.NotesRefusal(text: null, clear: false);

        Assert.NotNull(refusal);
        Assert.Equal("notes_required", refusal.Value.Code);
        Assert.Contains("--file <notes.md>", refusal.Value.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_notes_are_not_notes(string text) =>
        Assert.Equal("notes_required", TaskCommands.NotesRefusal(text, clear: false)!.Value.Code);

    [Fact]
    public void Notes_alongside_clear_is_a_contradiction() =>
        Assert.Equal("Pass --file or --notes, or --clear, not both.", TaskCommands.NotesRefusal("some notes", clear: true)!.Value.Message);

    [Fact]
    public void A_transcript_is_refused_before_it_is_sent()
    {
        var refusal = TaskCommands.NotesRefusal(new string('x', TaskNotesRequest.MaxChars + 1), clear: false);

        Assert.Equal("notes_too_long", refusal!.Value.Code);
    }

    [Fact]
    public void Notes_within_the_limit_or_a_clear_are_sent()
    {
        Assert.Null(TaskCommands.NotesRefusal("what the next session needs", clear: false));
        Assert.Null(TaskCommands.NotesRefusal(null, clear: true));
    }
}
