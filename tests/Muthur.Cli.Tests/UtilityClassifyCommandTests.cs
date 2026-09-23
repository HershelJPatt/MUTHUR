using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>`muthur utility classify` sits beside summarize, needs a task, and sends the task's title and body and nothing else.</summary>
public sealed class UtilityClassifyCommandTests
{
    private static RootCommand Root()
    {
        var root = new RootCommand("test");
        UtilityCommands.AddTo(root);
        return root;
    }

    [Fact]
    public void The_command_takes_a_task_and_an_optional_model()
    {
        var root = Root();
        Assert.Empty(root.Parse(["utility", "classify", "--task", "T-3"]).Errors);
        Assert.Empty(root.Parse(["utility", "classify", "--task", "T-3", "--model", "qwen"]).Errors);
        Assert.NotEmpty(root.Parse(["utility", "classify"]).Errors);
        var utility = root.Subcommands.Single(c => c.Name == "utility");
        Assert.Equal(["summarize", "classify"], utility.Subcommands.Select(c => c.Name));
    }

    [Fact]
    public async Task With_no_hub_it_fails_before_any_local_call()
    {
        var root = Root();
        Globals.AddTo(root);
        Assert.NotEqual(0, await root.Parse(["utility", "classify", "--task", "T-3"]).InvokeAsync());
    }

    [Fact]
    public void The_input_is_the_title_and_the_body()
    {
        Assert.Equal("Rename Foo", UtilityCommands.ClassifyInput("Rename Foo", ""));
        Assert.Equal("Rename Foo\n\nEverywhere.", UtilityCommands.ClassifyInput("Rename Foo", "Everywhere."));
    }
}
