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
    /// The repository paths the spec's units say they produce: every path in a <c>Files:</c> entry under a
    /// <c>Unit</c> heading, or anywhere in the spec when it has no unit headings. Empty when no unit lists files.
    /// </summary>
    public IReadOnlyList<string> Produced { get; init; } = [];

    /// <summary>Every repository path a verification command names, with the command that names it, in order.</summary>
    public IReadOnlyList<VerificationPath> Paths => [.. Commands.SelectMany(command => CommandPaths(command).Select(path => new VerificationPath(command, path)))];

    /// <summary>
    /// Whether some unit lists <paramref name="path"/>: the same path, a directory it sits under, a file under it when
    /// it is a directory, or an abbreviated listing (<c>Components/X.razor</c>) that it ends with.
    /// </summary>
    public bool IsProduced(string path) => Produced.Any(listed =>
        path.Equals(listed, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(listed + "/", StringComparison.OrdinalIgnoreCase)
        || listed.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/" + listed, StringComparison.OrdinalIgnoreCase));

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
        return new SpecVerification(commands, distinct.SingleOrDefault(), needs, hasSection) { Prose = prose, Produced = ProducedFiles(spec) };
    }

    [GeneratedRegex(@"^(#{2,6})\s+[*_`]*(.*?)\s*$")]
    private static partial Regex SubHeading();

    [GeneratedRegex(@"^Unit\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnitTitle();

    /// <summary>A <c>Files:</c> entry however it is marked up: <c>- **Files:** ...</c>, <c>Files: ...</c>, <c>* _Files_: ...</c>.</summary>
    [GeneratedRegex(@"^(\s*)[-*+>]?\s*[*_]*Files[*_]*\s*:\s*[*_]*\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FilesEntry();

    [GeneratedRegex(@"^(\s*)[-*+]\s")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpan();

    /// <summary>What the units list under <c>Files:</c>, with the entry's continuation lines and nested bullets.</summary>
    private static List<string> ProducedFiles(string spec)
    {
        var lines = spec.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var hasUnits = lines.Any(line => SubHeading().Match(line.Trim()) is { Success: true } m && UnitTitle().IsMatch(m.Groups[2].Value));
        var produced = new List<string>();
        int? unitLevel = null;
        var inFence = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal)) inFence = !inFence;
            if (inFence) continue;
            if (SubHeading().Match(text) is { Success: true } heading)
            {
                var level = heading.Groups[1].Length;
                if (UnitTitle().IsMatch(heading.Groups[2].Value)) unitLevel = level;
                else if (level <= unitLevel) unitLevel = null;
                continue;
            }
            if (hasUnits && unitLevel is null || FilesEntry().Match(lines[i]) is not { Success: true } entry) continue;
            var indent = entry.Groups[1].Length;
            var listed = new List<string> { entry.Groups[2].Value };
            for (; i + 1 < lines.Count; i++)
            {
                var next = lines[i + 1];
                if (next.Trim().Length == 0 || next.TrimStart().StartsWith('#')) break;
                if (Bullet().Match(next) is { Success: true } bullet && bullet.Groups[1].Length <= indent) break;
                listed.Add(next);
            }
            produced.AddRange(listed.SelectMany(ListedPaths));
        }
        return [.. produced.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The paths in one line of a Files entry: its code spans when it has any, otherwise its path-like words.</summary>
    private static IEnumerable<string> ListedPaths(string line)
    {
        var spans = CodeSpan().Matches(line).Select(m => m.Groups[1].Value).ToList();
        var words = spans.Count > 0 ? spans : line.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('*', '_', '(', ')', '.', ':'));
        foreach (var word in words)
            if (RepositoryPath(word.Trim()) is { } path) yield return path;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.@+/\\-]+$")]
    private static partial Regex PathCharacters();

    [GeneratedRegex(@"\.[a-z][a-z0-9]{0,7}$")]
    private static partial Regex Extension();

    /// <summary>A git object name followed by a path, <c>HEAD:T-2.txt</c>: the path is what the command reads. A drive letter is not a revision.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.@^~/-]{2,}:([^:]+)$")]
    private static partial Regex RevisionPath();

    /// <summary>Flags whose value a command writes rather than reads.</summary>
    private static readonly HashSet<string> OutputFlags = new(StringComparer.OrdinalIgnoreCase) { "-o", "--output", "--out", "--results-directory", "--diag", "-OutFile" };

    /// <summary>
    /// The repository-relative files one verification command reads: arguments whose last segment ends in a lowercase
    /// file extension, so directories, branch names (<c>task/T-1-x</c>) and dotted test filters are not files. Command
    /// names, flags, text with spaces or shell syntax in it, redirect targets, output-flag values, comments, absolute
    /// paths and anything that climbs out with <c>..</c> are skipped, and so is a <c>$variable = ...</c> assignment,
    /// which computes a path rather than reading one.
    /// </summary>
    public static IReadOnlyList<string> CommandPaths(string command)
    {
        var paths = new List<string>();
        if (command.TrimStart().StartsWith('$')) return paths;
        var atCommand = true;
        var skip = false;
        foreach (var (token, quoted) in Tokens(command))
        {
            if (!quoted && token.StartsWith('#')) break;
            if (!quoted && token is "|" or ";" or "&")
            {
                atCommand = true;
                continue;
            }
            if (!quoted && token == ">")
            {
                skip = true;
                continue;
            }
            if (skip || atCommand)
            {
                skip = atCommand = false;
                continue;
            }
            if (OutputFlags.Contains(token)) skip = true;
            else if (RepositoryPath(RevisionPath().Match(token) is { Success: true } revision ? revision.Groups[1].Value : token) is { } path
                && Extension().IsMatch(path))
                paths.Add(path);
        }
        return paths;
    }

    /// <summary>Forward slashes, no leading <c>./</c> or trailing slash; null when it is not a relative path inside the repository.</summary>
    private static string? RepositoryPath(string token)
    {
        if (token.StartsWith('-') || token.StartsWith('/') || token.StartsWith('\\') || !PathCharacters().IsMatch(token)) return null;
        var path = token.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        path = path.TrimEnd('/');
        if (path.Length == 0 || path.Split('/').Any(segment => segment is "" or "." or "..")) return null;
        return token.Contains('/') || token.Contains('\\') || Extension().IsMatch(path) ? path : null;
    }

    /// <summary>Whitespace-separated words with their quotes removed; <c>|</c>, <c>;</c>, <c>&amp;</c> and <c>&gt;</c> outside quotes are words of their own.</summary>
    private static IEnumerable<(string Token, bool Quoted)> Tokens(string command)
    {
        var word = new System.Text.StringBuilder();
        var quoted = false;
        char? quote = null;
        foreach (var c in command)
        {
            if (quote is { } open)
            {
                if (c == open) quote = null;
                else word.Append(c);
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                quoted = true;
                continue;
            }
            if (char.IsWhiteSpace(c) || c is '|' or ';' or '&' or '>')
            {
                if (word.Length > 0 || quoted) yield return (word.ToString(), quoted);
                word.Clear();
                quoted = false;
                if (!char.IsWhiteSpace(c)) yield return (c.ToString(), false);
                continue;
            }
            word.Append(c);
        }
        if (word.Length > 0 || quoted) yield return (word.ToString(), quoted);
    }

    private static string Clean(string value) => value.Trim().TrimEnd('.', '*', '`', '_').Trim();
}

/// <summary>One repository path a verification command names, and the command line it appears on.</summary>
public sealed record VerificationPath(string Command, string Path);
