using System.Text;
using System.Text.RegularExpressions;

namespace Muthur.Core;

public sealed record SecretFinding(string Kind, string Excerpt);

/// <summary>
/// Looks for credentials in text that is about to leave the machine. Deliberately biased toward false
/// positives: a blocked message costs a rewrite, a leaked key costs a rotation (or worse).
/// It is a net, not a proof: text can always be encoded past a pattern matcher, which is why a second agent
/// (and for some targets the founder) reads the exact bytes as well.
/// </summary>
public static partial class SecretScanner
{
    /// <summary>Distinctive token shapes. Also run on a compacted copy of the text, so a token split by whitespace, markup or zero-width characters is still found.</summary>
    private static readonly (string Kind, Regex Pattern)[] TokenPatterns =
    [
        ("private-key", PrivateKey()),
        ("aws-access-key", AwsAccessKey()),
        ("github-token", GitHubToken()),
        ("api-key", PrefixedApiKey()),
        ("slack-token", SlackToken()),
        ("jwt", Jwt()),
        ("sas-signature", SasSignature()),
        ("webhook-url", WebhookUrl()),
    ];

    /// <summary>Shapes that depend on the words around them; only meaningful on the text as written.</summary>
    private static readonly (string Kind, Regex Pattern)[] ContextPatterns =
    [
        ("bearer-token", Bearer()),
        ("connection-string-secret", ConnectionStringSecret()),
        ("credential-assignment", CredentialAssignment()),
        ("token-assignment", TokenAssignment()),
        ("url-credentials", UrlCredentials()),
    ];

    public static IReadOnlyList<SecretFinding> Scan(string text)
    {
        var findings = new List<SecretFinding>();
        var visible = RemoveInvisible(text);
        foreach (var (kind, pattern) in TokenPatterns.Concat(ContextPatterns))
            foreach (Match match in pattern.Matches(visible))
                findings.Add(new SecretFinding(kind, Mask(match.Value)));

        var compact = Compact(visible);
        foreach (var (kind, pattern) in TokenPatterns)
            foreach (Match match in pattern.Matches(compact))
                if (!findings.Any(f => f.Kind == kind))
                    findings.Add(new SecretFinding(kind + " (split across whitespace or markup)", Mask(match.Value)));
        return findings;
    }

    /// <summary>Zero-width and other format characters are invisible to a reviewer and break patterns; drop them.</summary>
    private static string RemoveInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>The text with markup tags and all whitespace removed.</summary>
    private static string Compact(string text) => Whitespace().Replace(Tags().Replace(text, ""), "");

    /// <summary>Enough to find it in the draft, never enough to use it.</summary>
    private static string Mask(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= 12 ? new string('*', value.Length) : $"{value[..6]}…{new string('*', 6)} ({value.Length} chars)";
    }

    [GeneratedRegex(@"<[^<>]{1,200}>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"-----BEGIN\s*(?:RSA|EC|OPENSSH|DSA|PGP|ENCRYPTED)?\s*PRIVATE\s*KEY(?:\s*BLOCK)?-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})")]
    private static partial Regex GitHubToken();

    // Vendor-prefixed keys: sk-…, sk-ant-…, sk_live_…, rk_live_…, xai-…, AIza…, npm_…, glpat-…, pypi-…, dop_v1_…, shpat_…, hf_…
    [GeneratedRegex(@"(?:sk-(?:ant-|proj-|live-|test-)?[A-Za-z0-9_\-]{20,}|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}|xai-[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{30,}|npm_[A-Za-z0-9]{30,}|glpat-[A-Za-z0-9_\-]{20,}|pypi-[A-Za-z0-9_\-]{30,}|dop_v1_[a-f0-9]{40,}|shp(?:at|ss|ca)_[a-fA-F0-9]{30,}|hf_[A-Za-z0-9]{30,})")]
    private static partial Regex PrefixedApiKey();

    [GeneratedRegex(@"xox[abprs]-[A-Za-z0-9\-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"[?&]sig=[A-Za-z0-9%+/=]{20,}")]
    private static partial Regex SasSignature();

    [GeneratedRegex(@"https://(?:discord(?:app)?\.com/api/webhooks|hooks\.slack\.com/services)/[A-Za-z0-9/_\-]{20,}")]
    private static partial Regex WebhookUrl();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9_\-\.=]{20,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(?:Password|Pwd|AccountKey|SharedAccessKey|SharedSecret)\s*=\s*[^;\s""']{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecret();

    // password: hunter22 · "api_key": "…" · client_secret='…' · aws_secret_access_key = … · token: 9f86d0…
    // The key may be quoted (JSON, YAML); the value is 8+ characters without whitespace.
    [GeneratedRegex(@"[""']?\b(?:[a-z0-9_\-]*(?:password|passwd|secret|api[_\-]?key|access[_\-]?key|private[_\-]?key|auth[_\-]?token|access[_\-]?token|refresh[_\-]?token)|pwd)\b[""']?\s*[:=]\s*[""']?[^\s""',;}]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignment();

    // token: 9f86d081884c7d65… — "token" is an ordinary word, so only a long opaque value counts
    [GeneratedRegex(@"[""']?\btoken\b[""']?\s*[:=]\s*[""']?[A-Za-z0-9_\-+/=.]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenAssignment();

    // scheme://user:password@host — any scheme (postgres, mongodb+srv, redis, https, amqp…); an empty user is allowed (redis://:pw@…)
    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]{1,20}://[^\s/:@]*:[^\s/@]{3,}@[^\s/]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
}
