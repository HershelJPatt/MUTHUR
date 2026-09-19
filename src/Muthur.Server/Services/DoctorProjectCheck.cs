using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>Whether each project is gated by validators that exist, and says out loud how to build itself.</summary>
public sealed class DoctorProjectCheck(Ledger ledger) : IDoctorCheck
{
    private const string Manifest = "muthur.project.json";
    private static readonly string[] RequiredFields = ["key", "build", "test"];

    public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<CheckDto>>(async (db, _) =>
        {
            var projects = await db.Projects.OrderBy(p => p.Key).ToListAsync(ct);
            var roles = await db.Roles.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, ct);

            var checks = new List<CheckDto>();
            foreach (var project in projects)
            {
                checks.Add(Gate(project, roles));
                if (await ManifestAsync(project, ct) is { } manifest) checks.Add(manifest);
            }
            return checks;
        }, ct);

    private static CheckDto Gate(Project project, IReadOnlyDictionary<string, Role> roles)
    {
        if (project.RequiredValidators.Count == 0)
            return Check(CheckStatus.Warn, "No required validators: every task in this project goes from implemented straight " +
                $"to validated with nobody looking. muthur project set {project.Key} --validator <role>");

        if (project.RequiredValidators.FirstOrDefault(v => !roles.ContainsKey(v)) is { } undefined)
            return Check(CheckStatus.Fail, $"Requires validator '{undefined}', which is not a defined role. muthur role define {undefined} --founder");

        if (project.RequiredValidators.FirstOrDefault(v => !roles[v].IsValidator) is { } silent)
            return Check(CheckStatus.Fail, $"Requires validator '{silent}', a role that may not give verdicts. muthur role define {silent} --validator true --founder");

        return Check(CheckStatus.Ok, $"Gated by {string.Join(", ", project.RequiredValidators)}.");

        CheckDto Check(CheckStatus status, string detail) => new("project", project.Key, status, detail);
    }

    /// <summary>Null when the repository directory is missing: <see cref="DoctorRepoCheck"/> says that once, for the project.</summary>
    private static async Task<CheckDto?> ManifestAsync(Project project, CancellationToken ct)
    {
        if (!Directory.Exists(project.RepoPath)) return null;

        var path = Path.Combine(project.RepoPath, Manifest);
        if (!File.Exists(path))
            return Check(CheckStatus.Warn, $"No {Manifest} in {project.RepoPath}: agents have no build, test or run commands for this project.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        }
        catch (JsonException ex)
        {
            return Check(CheckStatus.Fail, $"{Manifest} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var missing = RequiredFields.Where(field => Text(document.RootElement, field) is null).ToList();
            if (missing.Count > 0)
                return Check(CheckStatus.Warn, $"{Manifest} has no {string.Join(", ", missing)}.");

            var key = Text(document.RootElement, "key");
            return key == project.Key
                ? Check(CheckStatus.Ok, $"{Manifest} names how to build, test and run it.")
                : Check(CheckStatus.Warn, $"{Manifest} says key '{key}'; this project is '{project.Key}'.");
        }

        CheckDto Check(CheckStatus status, string detail) => new("project", project.Key, status, detail);
    }

    /// <summary>The field's text, or null when it is absent, blank, or something other than a string — all of which are "not set".</summary>
    private static string? Text(JsonElement root, string field) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && text.Trim().Length > 0
            ? text
            : null;
}
