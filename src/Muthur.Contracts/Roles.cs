namespace Muthur.Contracts;

public sealed record DefineRoleRequest(string Key, string? Brief = null, bool? IsValidator = null, int? Holders = null);

/// <param name="Agent">The agent holding it. One row per live hold.</param>
public sealed record RoleHolderDto(string Agent, DateTimeOffset HeldSince, DateTimeOffset LeaseExpires);

/// <param name="Capacity">How many agents may hold this role at once. Always 1 for a non-validator role.</param>
public sealed record RoleDto(
    string Key,
    bool IsValidator,
    int Capacity,
    IReadOnlyList<RoleHolderDto> Holders,
    bool HasBrief,
    DateTimeOffset UpdatedAt);

public sealed record RoleBriefDto(string Key, bool IsValidator, string Brief, DateTimeOffset UpdatedAt);

public sealed record ImplementedRequest(string Branch);

public sealed record VerdictRequest(string Validator, string? Evidence = null);

public sealed record ClaimValidationRequest(string Validator);
