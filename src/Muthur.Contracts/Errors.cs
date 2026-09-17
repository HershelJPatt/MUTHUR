namespace Muthur.Contracts;

/// <summary>Body of every non-2xx API response. <see cref="Code"/> is stable and machine-readable.</summary>
public sealed record ErrorResponse(string Code, string Message);

/// <summary>Process exit codes of the CLI. Agents branch on these.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int RuleViolation = 2;
    public const int Conflict = 3;
    public const int NotRunning = 4;
    public const int NotFound = 5;
    public const int Unauthorized = 6;
}
