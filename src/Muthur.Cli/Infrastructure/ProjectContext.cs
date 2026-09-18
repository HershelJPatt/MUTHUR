using System.Text.Json;

namespace Muthur.Cli.Infrastructure;

/// <summary>Finds the project of the current directory by walking up to the nearest muthur.project.json.</summary>
public static class ProjectContext
{
    public const string FileName = "muthur.project.json";

    public static string? FindFile(string? startDirectory = null)
    {
        for (var dir = new DirectoryInfo(startDirectory ?? Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? FindKey(string? startDirectory = null)
    {
        if (FindFile(startDirectory) is not { } file) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("key", out var key) ? key.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
