namespace Muthur.Contracts;

public sealed record DefineRoleRequest(string Key, string? Brief = null, bool? IsValidator = null);

public sealed record RoleDto(
    string Key,
    bool IsValidator,
    string? Holder,
    DateTimeOffset? HeldSince,
    DateTimeOffset? LeaseExpires,
    bool HasBrief,
    DateTimeOffset UpdatedAt);

public sealed record RoleBriefDto(string Key, bool IsValidator, string Brief, DateTimeOffset UpdatedAt);

public sealed record ImplementedRequest(string Branch);

public sealed record VerdictRequest(string Validator, string? Evidence = null);
