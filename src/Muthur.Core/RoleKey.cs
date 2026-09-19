using System.Text.RegularExpressions;

namespace Muthur.Core;

/// <summary>
/// What may name a role. Projects name required validators with these, and the conductor builds agent
/// identities out of them, so the rule has to be one rule rather than a literal repeated per caller.
/// </summary>
public static partial class RoleKey
{
    /// <summary>
    /// The longest a role key may be. Callers that build something out of a key budget against this: the
    /// conductor's identity is "conductor-{role}" and an agent name may be 48 characters, so a longer key
    /// would name a role whose conductor could never register. Pinned by the assertion in ConductorTests.
    /// </summary>
    public const int MaxLength = 38;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,37}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    /// <summary>The rule in the words the founder reads when they break it.</summary>
    public const string Rule = "1-38 chars of a-z, 0-9 or '-'";
}
