using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

public sealed class TaskUnitCommandTests
{
    [Theory]
    [InlineData("units", "T-1")]
    [InlineData("units", "T-1", "--define", "graph.json")]
    [InlineData("checkpoint", "T-1", "--file", "transition.json")]
    [InlineData("resume", "T-1")]
    public void Json_commands_are_wired_under_task(params string[] arguments)
    {
        var root = new RootCommand();
        TaskCommands.AddTo(root);
        var parsed = root.Parse(["task", .. arguments]);
        Assert.Empty(parsed.Errors);
        Assert.NotNull(parsed.CommandResult.Command.Action);
    }

    [Fact]
    public void Checkpoint_requires_an_explicit_json_file()
    {
        var root = new RootCommand();
        TaskCommands.AddTo(root);
        Assert.NotEmpty(root.Parse("task checkpoint T-1").Errors);
        Assert.Equal("/api/v1/tasks/T-1/units", Routes.TaskUnits("T-1"));
        Assert.Equal("/api/v1/tasks/T-1/resume", Routes.TaskResume("T-1"));
    }

    [Fact]
    public async Task File_reader_preserves_cas_and_proof_with_source_generated_metadata()
    {
        var file = Path.GetTempFileName();
        try
        {
            var request = new TaskUnitCheckpointRequest(123, "a", Guid.NewGuid(), "verify",
                Checks: [new("test", 0, "evidence/check.txt", new string('a', 40))], ReviewEvidencePath: "evidence/review.txt",
                ReviewEvidenceCommit: new string('b', 40));
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(request, MuthurJsonContext.Default.TaskUnitCheckpointRequest));
            var read = await TaskUnitCommands.ReadFile(file, MuthurJsonContext.Default.TaskUnitCheckpointRequest, default);
            Assert.Equal(request.ExpectedRevision, read.ExpectedRevision);
            Assert.Equal(request.AttemptId, read.AttemptId);
            Assert.Equal(request.Checks, read.Checks);
            Assert.Equal(request.ReviewEvidenceCommit, read.ReviewEvidenceCommit);
            await File.WriteAllTextAsync(file, "null");
            await Assert.ThrowsAsync<JsonException>(() => TaskUnitCommands.ReadFile(file, MuthurJsonContext.Default.TaskUnitCheckpointRequest, default));
        }
        finally { File.Delete(file); }
    }
}
