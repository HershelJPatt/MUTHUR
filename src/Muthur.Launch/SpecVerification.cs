using System.Text.RegularExpressions;

namespace Muthur.Launch;

/// <summary>
/// What a frozen spec says about how it is verified: the commands in fenced blocks under <c>## Verification</c>,
/// the standalone <c>validation:</c> and <c>needs:</c> lines, and whether the section exists at all.
/// </summary>
/// <param name="Commands">Every non-blank, non-comment line of a fenced block under the Verification heading, in order.</param>
/// <param name="Validation"><c>checks-only</c> or <c>judgment</c> when the spec says so on a line of its own; null otherwise.</param>
/// <param name="Needs">The comma-separated values of every standalone <c>needs:</c> line, anywhere in the spec.</param>
/// <param name="HasSection">Whether a <c>## Verification</c> heading exists.</param>
public sealed partial record SpecVerification(IReadOnlyList<string> Commands, string? Validation, IReadOnlyList<string> Needs, bool HasSection)
{
    public const string ChecksOnly = "checks-only";
    public const string Judgment = "judgment";

    /// <summary>The prose of the Verification section: its lines outside fenced blocks, trimmed, blanks dropped.</summary>
    public IReadOnlyList<string> Prose { get; init; } = [];

    /// <summary>
    /// Whether a model has to look at this. A spec that says <c>validation: judgment</c>, declares a need, has no
    /// Verification section, or lists no command has nothing a script could decide on its own; anything else can
    /// be verified by running what it wrote down.
    /// </summary>
    public bool RequiresJudgment => Validation == Judgment || Needs.Count > 0 || !HasSection || Commands.Count == 0;

    [GeneratedRegex(@"^##\s+Verification\b", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^#{1,2}\s")]
    private static partial Regex AnyHeading();

    /// <summary>Same tolerance as the hub's <c>needs:</c> reader: a leading list, quote or emphasis marker is not the line's meaning.</summary>
    [GeneratedRegex(@"^[\s>*_`-]*(needs|validation):[\s*_`]*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Declaration();

    public static SpecVerification Parse(string spec)
    {
        var commands = new List<string>();
        var prose = new List<string>();
        var needs = new List<string>();
        var validations = new List<string>();
        var hasSection = false;
        var inSection = false;
        var inFence = false;
        foreach (var raw in spec.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var text = line.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                if (inSection && text.Length > 0 && !text.StartsWith('#')) commands.Add(text);
                continue;
            }
            if (Heading().IsMatch(text))
            {
                hasSection = inSection = true;
                continue;
            }
            if (inSection && AnyHeading().IsMatch(text)) inSection = false;
            if (Declaration().Match(text) is { Success: true } match)
            {
                var values = match.Groups[2].Value.Split(',').Select(Clean).Where(v => v.Length > 0);
                if (match.Groups[1].Value.Equals("needs", StringComparison.OrdinalIgnoreCase)) needs.AddRange(values);
                else validations.AddRange(values.Select(v => v.ToLowerInvariant()));
            }
            else if (inSection && text.Length > 0) prose.Add(text);
        }
        var distinct = validations.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1 || distinct.Count == 1 && distinct[0] is not (ChecksOnly or Judgment))
            throw new WorkerDispatchException("spec_validation_invalid",
                $"Use one consistent validation line: 'validation: {ChecksOnly}' or 'validation: {Judgment}'.");
        return new SpecVerification(commands, distinct.SingleOrDefault(), needs, hasSection) { Prose = prose };
    }

    private static string Clean(string value) => value.Trim().TrimEnd('.', '*', '`', '_').Trim();
}
