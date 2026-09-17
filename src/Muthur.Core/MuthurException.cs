namespace Muthur.Core;

public enum ErrorKind
{
    /// <summary>The request breaks a rule of the organization (state machine, gate, role). CLI exit 2.</summary>
    RuleViolation,
    /// <summary>Someone else got there first, or the target changed underneath the caller. CLI exit 3.</summary>
    Conflict,
    NotFound,
    Unauthorized,
}

/// <summary>The only exception type services throw on purpose. <see cref="Code"/> is a stable identifier agents can match on.</summary>
public sealed class MuthurException(ErrorKind kind, string code, string message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
    public string Code { get; } = code;
}

public static class Fail
{
    public static MuthurException Rule(string code, string message) => new(ErrorKind.RuleViolation, code, message);
    public static MuthurException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static MuthurException NotFound(string what, string id) => new(ErrorKind.NotFound, "not_found", $"{what} '{id}' does not exist.");
    public static MuthurException Unauthorized(string message) => new(ErrorKind.Unauthorized, "unauthorized", message);
}
