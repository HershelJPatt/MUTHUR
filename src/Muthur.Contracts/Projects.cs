namespace Muthur.Contracts;

public sealed record AddProjectRequest(
    string Key,
    string RepoPath,
    string? Name = null,
    string? DefaultBranch = null,
    LandMode? LandMode = null,
    IReadOnlyList<string>? RequiredValidators = null,
    IReadOnlyList<string>? IngestSources = null);

public sealed record UpdateProjectRequest(
    string? RepoPath = null,
    string? Name = null,
    string? DefaultBranch = null,
    LandMode? LandMode = null,
    IReadOnlyList<string>? RequiredValidators = null,
    IReadOnlyList<string>? IngestSources = null);

public sealed record ProjectDto(
    Guid Id,
    string Key,
    string Name,
    string RepoPath,
    string DefaultBranch,
    LandMode LandMode,
    IReadOnlyList<string> RequiredValidators,
    DateTimeOffset CreatedAt,
    /// <summary>External sources polled for inbound items, e.g. "github:owner/repo".</summary>
    IReadOnlyList<string> IngestSources);
