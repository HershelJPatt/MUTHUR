using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public sealed partial class ProjectService(Ledger ledger, IEnumerable<IInboundSource> sources)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex KeyPattern();

    public Task<ProjectDto> AddAsync(Caller caller, AddProjectRequest request, CancellationToken ct = default)
    {
        RequireFounder(caller);
        var key = (request.Key ?? "").Trim().ToLowerInvariant();
        if (!KeyPattern().IsMatch(key))
            throw Fail.Rule("invalid_key", "Project keys are 1-32 chars of a-z, 0-9 or '-'.");
        if (string.IsNullOrWhiteSpace(request.RepoPath))
            throw Fail.Rule("repo_required", "A project needs the path of its repository.");

        return ledger.MutateAsync(caller, async m =>
        {
            if (await m.Db.Projects.AnyAsync(p => p.Key == key, ct))
                throw Fail.Conflict("project_exists", $"Project '{key}' already exists.");
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Key = key,
                Name = string.IsNullOrWhiteSpace(request.Name) ? key : request.Name.Trim(),
                RepoPath = Path.GetFullPath(request.RepoPath),
                DefaultBranch = string.IsNullOrWhiteSpace(request.DefaultBranch) ? "main" : request.DefaultBranch.Trim(),
                LandMode = request.LandMode ?? LandMode.Merge,
                RequiredValidators = ValidValidators(request.RequiredValidators),
                IngestSources = ValidSources(request.IngestSources),
                CreatedAt = m.Now,
            };
            m.Db.Projects.Add(project);
            m.Record("project.added", payload: new { project = key, project.RepoPath, landMode = project.LandMode.ToWire(), project.RequiredValidators, ungated = project.RequiredValidators.Count == 0, project.IngestSources });
            return project.ToDto();
        }, ct);
    }

    public Task<ProjectDto> UpdateAsync(Caller caller, string key, UpdateProjectRequest request, CancellationToken ct = default)
    {
        RequireFounder(caller);
        return ledger.MutateAsync(caller, async m =>
        {
            var project = await FindAsync(m.Db, key, ct);
            if (!string.IsNullOrWhiteSpace(request.Name)) project.Name = request.Name.Trim();
            if (!string.IsNullOrWhiteSpace(request.RepoPath)) project.RepoPath = Path.GetFullPath(request.RepoPath);
            if (!string.IsNullOrWhiteSpace(request.DefaultBranch)) project.DefaultBranch = request.DefaultBranch.Trim();
            if (request.LandMode is { } mode) project.LandMode = mode;
            if (request.RequiredValidators is not null) project.RequiredValidators = ValidValidators(request.RequiredValidators);
            if (request.IngestSources is not null) project.IngestSources = ValidSources(request.IngestSources);
            m.Record("project.updated", payload: new { project = project.Key, project.RepoPath, landMode = project.LandMode.ToWire(), project.RequiredValidators, ungated = project.RequiredValidators.Count == 0, project.IngestSources });
            return project.ToDto();
        }, ct);
    }

    public Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<ProjectDto>>(async (db, _) =>
            (await db.Projects.OrderBy(p => p.Key).ToListAsync(ct)).Select(p => p.ToDto()).ToList(), ct);

    public Task<ProjectDto> GetAsync(string key, CancellationToken ct = default) =>
        ledger.ReadAsync(async (db, _) => (await FindAsync(db, key, ct)).ToDto(), ct);

    /// <summary>Resolves an explicit key, or the only project when none is given.</summary>
    public static async Task<Project> FindAsync(MuthurDb db, string? key, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var normalized = key.Trim().ToLowerInvariant();
            return await db.Projects.SingleOrDefaultAsync(p => p.Key == normalized, ct) ?? throw Fail.NotFound("Project", normalized);
        }
        var all = await db.Projects.OrderBy(p => p.Key).Take(2).ToListAsync(ct);
        return all.Count switch
        {
            1 => all[0],
            0 => throw Fail.Rule("no_projects", "No project exists yet. Create one with: muthur project add <key> --repo <path> --founder"),
            _ => throw Fail.Rule("project_required", "More than one project exists; pass --project <key>."),
        };
    }

    /// <summary>A source the hub has no adapter for would fail on every poll forever; refuse it when it is configured.</summary>
    private List<string> ValidSources(IReadOnlyList<string>? requested)
    {
        var normalized = Normalize(requested);
        foreach (var source in normalized)
        {
            var colon = source.IndexOf(':');
            if (colon <= 0 || colon == source.Length - 1 || !sources.Any(s => s.Scheme == source[..colon]))
                throw Fail.Rule("unknown_ingest_source", $"'{source}' is not a source this hub can poll. Expected scheme:location with scheme one of: {string.Join(", ", sources.Select(s => s.Scheme))}.");
        }
        return normalized;
    }

    /// <summary>
    /// A required validator names a role, so a string no role key could ever be names nothing and would gate
    /// nothing. Whether the role exists yet is not checked here — `muthur doctor` says that out loud instead,
    /// because setting a project up before defining its roles is reasonable.
    /// </summary>
    private static List<string> ValidValidators(IReadOnlyList<string>? requested)
    {
        var normalized = Normalize(requested);
        foreach (var key in normalized)
            if (!RoleKey.IsValid(key))
                throw Fail.Rule("invalid_validator",
                    $"'{key}' cannot name a role, so no validator could ever hold it. Role keys are {RoleKey.Rule}.");
        return normalized;
    }

    private static List<string> Normalize(IReadOnlyList<string>? validators) =>
        (validators ?? []).Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).Distinct().ToList();

    private static void RequireFounder(Caller caller)
    {
        if (!caller.IsFounder)
            throw Fail.Unauthorized("Projects define repositories and merge authority; only the founder may change them (pass --founder).");
    }
}
