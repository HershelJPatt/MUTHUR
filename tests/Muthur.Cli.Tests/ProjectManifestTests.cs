using System.Text.Json;
using Muthur.Cli.Infrastructure;

namespace Muthur.Cli.Tests;

/// <summary>
/// This repository's own muthur.project.json held an unescaped Windows path, so the whole file failed to
/// parse and every reader swallowed the failure: `muthur worker run` found no build or test commands and
/// `--project` fell back to nothing, silently, for two days. These tests fail the build if it happens again.
/// </summary>
public sealed class ProjectManifestTests
{
    private static string Manifest =>
        ProjectContext.FindFile(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException(
            $"No {ProjectContext.FileName} found walking up from {AppContext.BaseDirectory}.");

    [Fact]
    public void The_repositorys_own_manifest_is_valid_json()
    {
        var file = Manifest;
        var exception = Record.Exception(() => JsonDocument.Parse(File.ReadAllText(file)).Dispose());

        Assert.True(exception is null, $"{file} is not valid JSON: {exception?.Message}");
    }

    [Fact]
    public void The_repositorys_own_manifest_names_its_key_and_its_commands()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Manifest));

        Assert.Equal("muthur", doc.RootElement.GetProperty("key").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("build").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("test").GetString()));
    }

    [Fact]
    public void The_key_is_readable_through_the_path_that_regressed()
    {
        var file = Manifest;
        var key = ProjectContext.FindKey(AppContext.BaseDirectory);

        Assert.True(key == "muthur", $"FindKey read '{key ?? "(null)"}' from {file}, which exists.");
    }
}
