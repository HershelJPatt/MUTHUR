using System.Text;
using System.Text.RegularExpressions;

namespace Muthur.Core;

public sealed record SecretFinding(string Kind, string Excerpt);

/// <summary>
/// Looks for credentials in text that is about to leave the machine. Deliberately biased toward false
/// positives: a blocked message costs a rewrite, a leaked key costs a rotation (or worse).
/// It is a net, not a proof: text can always be encoded past a pattern matcher, which is why a second agent
/// (and for some targets the founder) reads the exact bytes as well.
/// The expected behavior is pinned by the corpus in tests/Muthur.Core.Tests/SecretCorpus: a shape that slips
/// through becomes a line in block.txt first, then a pattern here.
/// </summary>
public static partial class SecretScanner
{
    /// <summary>Distinctive token shapes. Also matched on a joined copy of the text, so a token split by whitespace, markup, emphasis marks, string concatenation or invisible characters is still found.</summary>
    private static readonly (string Kind, Regex Pattern)[] TokenPatterns =
    [
        ("private-key", PrivateKey()),
        ("private-key", KeyMaterial()),
        ("aws-access-key", AwsAccessKey()),
        ("github-token", GitHubToken()),
        ("api-key", PrefixedApiKey()),
        ("api-key", StructuredVendorToken()),
        ("slack-token", SlackToken()),
        ("jwt", Jwt()),
        ("sas-signature", SasSignature()),
        ("webhook-url", WebhookUrl()),
        ("password-hash", PasswordHash()),
    ];

    /// <summary>Shapes that depend on the words around them; only meaningful on the text as written.</summary>
    private static readonly (string Kind, Regex Pattern)[] ContextPatterns =
    [
        ("authorization-header", AuthorizationHeader()),
        ("connection-string-secret", ConnectionStringSecret()),
        ("credential-assignment", StrongAssignment()),
        ("credential-assignment", WeakAssignment()),
        ("credential-in-prose", Prose()),
        ("credential-in-markup", XmlElement()),
        ("credential-in-markup", XmlAttributePair()),
        ("command-line-credential", CommandLine()),
        ("url-credentials", UrlCredentials()),
        ("opaque-value-near-keyword", OpaqueNearKeyword()),
    ];

    public static IReadOnlyList<SecretFinding> Scan(string text)
    {
        var findings = new List<SecretFinding>();
        var visible = RemoveInvisible(text);
        foreach (var (kind, pattern) in TokenPatterns.Concat(ContextPatterns))
            foreach (Match match in pattern.Matches(visible))
                if (!IsPlaceholder(match))
                    findings.Add(new SecretFinding(kind, Mask(match.Value)));

        var joined = Join(visible);
        foreach (var (kind, pattern) in TokenPatterns)
            foreach (Match match in pattern.Matches(joined))
                if (!findings.Any(f => f.Kind == kind))
                    findings.Add(new SecretFinding(kind + " (split across whitespace or markup)", Mask(match.Value)));
        return findings;
    }

    /// <summary>`API_KEY=$YOUR_KEY`, `password: &lt;your password&gt;`, `token={{TOKEN}}`: documentation, not a credential.</summary>
    private static bool IsPlaceholder(Match match)
    {
        var value = match.Groups["value"];
        if (!value.Success) return false;
        var v = value.Value.TrimStart('"', '\'');
        return v.StartsWith('$') || v.StartsWith('<') || v.StartsWith("{{", StringComparison.Ordinal) || v.StartsWith('%')
            || v.StartsWith("***", StringComparison.Ordinal) || v.StartsWith("xxx", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("your", StringComparison.OrdinalIgnoreCase) || v.StartsWith("example", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("changeme", StringComparison.OrdinalIgnoreCase) || v.StartsWith("redacted", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Zero-width and other format characters are invisible to a reviewer and break patterns; drop them.</summary>
    private static string RemoveInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>The text with everything removed that can sit inside a token without changing how a human reassembles it.</summary>
    private static string Join(string text) => Joiners().Replace(Tags().Replace(text, ""), "");

    /// <summary>Enough to find it in the draft, never enough to use it.</summary>
    private static string Mask(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= 12 ? new string('*', value.Length) : $"{value[..6]}…{new string('*', 6)} ({value.Length} chars)";
    }

    [GeneratedRegex(@"<[^<>]{1,200}>")]
    private static partial Regex Tags();

    // whitespace, markdown emphasis and code marks, line-continuation backslashes, quotes and + (string concatenation)
    [GeneratedRegex(@"[\s*`~\\""'+]+")]
    private static partial Regex Joiners();

    // ---- token shapes ---------------------------------------------------------------------------------------------

    [GeneratedRegex(@"-----BEGIN\s*(?:RSA|EC|OPENSSH|DSA|PGP|ENCRYPTED)?\s*PRIVATE\s*KEY(?:\s*BLOCK)?-----")]
    private static partial Regex PrivateKey();

    // Key bodies without their PEM header: "openssh-key-v1" in base64, PuTTY .ppk files, age identities
    [GeneratedRegex(@"b3BlbnNzaC1rZXktdjE|PuTTY-User-Key-File-\d|Private-Lines:\s*\d|AGE-SECRET-KEY-1[A-Z0-9]{20,}")]
    private static partial Regex KeyMaterial();

    [GeneratedRegex(@"(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})")]
    private static partial Regex GitHubToken();

    // Vendor-prefixed keys
    [GeneratedRegex(@"(?:sk-(?:ant-|proj-|live-|test-)?[A-Za-z0-9_\-]{20,}|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}|whsec_[A-Za-z0-9]{20,}|xai-[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{30,}|ya29\.[A-Za-z0-9_\-]{20,}|GOCSPX-[A-Za-z0-9_\-]{20,}|npm_[A-Za-z0-9]{30,}|glpat-[A-Za-z0-9_\-]{20,}|pypi-[A-Za-z0-9_\-]{30,}|dop_v1_[a-f0-9]{40,}|shp(?:at|ss|ca)_[a-fA-F0-9]{30,}|hf_[A-Za-z0-9]{30,}|hvs\.[A-Za-z0-9_\-]{20,}|lin_api_[A-Za-z0-9]{20,}|sq0(?:atp|csp)-[A-Za-z0-9_\-]{20,}|NRAK-[A-Z0-9]{20,}|key-[0-9a-f]{32})")]
    private static partial Regex PrefixedApiKey();

    // Keys recognizable by structure: SendGrid SG.x.y, Twilio AC/SK + 32 hex, Telegram bot id:secret, Discord bot token
    [GeneratedRegex(@"(?:SG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{16,}|(?<![A-Za-z0-9])(?:AC|SK)[0-9a-f]{32}(?![A-Za-z0-9])|(?<![0-9])\d{8,10}:[A-Za-z0-9_\-]{30,}|(?<![A-Za-z0-9])[MNO][A-Za-z0-9_\-]{23,25}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27,})")]
    private static partial Regex StructuredVendorToken();

    [GeneratedRegex(@"x(?:ox[abprs]|app)-[A-Za-z0-9\-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"[?&]sig=[A-Za-z0-9%+/=]{20,}")]
    private static partial Regex SasSignature();

    // URLs that are themselves the credential: chat webhooks, bot API endpoints
    [GeneratedRegex(@"https://(?:discord(?:app)?\.com/api/webhooks/|hooks\.slack\.com/services/|[A-Za-z0-9.\-]*webhook\.office\.com/webhookb2/|api\.telegram\.org/bot)[A-Za-z0-9/_\-:@.]{20,}")]
    private static partial Regex WebhookUrl();

    // htpasswd / shadow entries: $apr1$, bcrypt $2a$/$2b$/$2y$, sha512-crypt $6$
    [GeneratedRegex(@"\$(?:apr1|2[aby]|5|6)\$[A-Za-z0-9./$=]{20,}")]
    private static partial Regex PasswordHash();

    // ---- shapes that need their context ----------------------------------------------------------------------------

    [GeneratedRegex(@"\b(?:Bearer|Basic|Token)\s+(?<value>[A-Za-z0-9_\-\.=+/]{16,})|\bAuthorization\s*:\s*\S+\s+(?<value>\S{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"\b(?:Password|Pwd|AccountKey|SharedAccessKey|SharedSecret)\s*=\s*(?<value>[^;\s""']{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecret();

    // An identifier that CONTAINS password/passwd/passphrase/pass/pwd/pw/secret (SECRET_KEY_BASE, smtpPassword, DB_PASS, aws_secret_access_key_prod…)
    // followed by = : := => a tab or a full-width colon, then a value of 6+ characters. "passed", "passing", "compass", "bypass" are not credentials.
    [GeneratedRegex(@"[""']?(?<![A-Za-z])(?:[A-Za-z0-9_.\-]*?(?:(?<!com|by|tres)pass(?!ed\b|ing\b|es\b|ive|enger|port|age)(?:word|wd|phrase|wort|code)?|pwd|secret)[A-Za-z0-9_.\-]*|pw)[""']?[ \t]*(?:=>|:=|[:=：]|\t)[ \t]*(?:[|>][+\-]?[ \t]*\r?\n[ \t]*)?[""']?(?<value>[^\s""',;}<]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex StrongAssignment();

    // Identifiers containing token/key/credential/auth words are everyday vocabulary, so only a long opaque value counts.
    [GeneratedRegex(@"[""']?(?<![A-Za-z])[A-Za-z0-9_.\-]*?(?:token|api[_\-]?key|access[_\-]?key|private[_\-]?key|encryption[_\-]?key|signing[_\-]?key|secret[_\-]?key|credentials?|_auth|auth[_\-]?key)[A-Za-z0-9_.\-]*[""']?[ \t]*(?:=>|:=|[:=：]|\t)[ \t]*[""']?(?<value>[A-Za-z0-9_\-+/=.:]{16,})", RegexOptions.IgnoreCase)]
    private static partial Regex WeakAssignment();

    // "the password is hunter2hunter2", netrc "login bob password hunter2x" — a value with a digit in it, so "password is required" passes
    [GeneratedRegex(@"\bpass(?:word|phrase|code|wd)?\s+(?:(?:is|was|to|:|=)\s+)?(?<value>(?=\S*\d)[^\s.,;]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex Prose();

    [GeneratedRegex(@"<(?<tag>[A-Za-z0-9_.\-]*(?:pass|pwd|secret|token|apikey|api_key)[A-Za-z0-9_.\-]*)>\s*(?<value>[^<\s]{6,})\s*</", RegexOptions.IgnoreCase)]
    private static partial Regex XmlElement();

    // <add key="SmtpPassword" value="…"/>
    [GeneratedRegex(@"[""'][A-Za-z0-9_.\-]*(?:pass|pwd|secret|token|apikey|api_key)[A-Za-z0-9_.\-]*[""']\s+value\s*=\s*[""'](?<value>[^""']{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex XmlAttributePair();

    // curl -u user:pass · --user user:pass · mysql -pSECRET · docker/npm "auth": "<base64>"
    [GeneratedRegex(@"(?:(?<![A-Za-z0-9])-u|--user)\s+[^\s:]+:(?<value>\S{4,})|\bmysql\b[^\n]*\s-p(?<value>\S{6,})|[""']auth[""']\s*:\s*[""'](?<value>[A-Za-z0-9+/=]{12,})", RegexOptions.IgnoreCase)]
    private static partial Regex CommandLine();

    // scheme://user:password@host — any scheme (postgres, mongodb+srv, redis, https, amqp…); an empty user is allowed (redis://:pw@…)
    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]{1,20}://[^\s/:@]*:(?<value>[^\s/@]{3,})@[^\s/]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    // A long opaque run (letters AND digits) on a line that talks about keys, secrets, tokens or passwords
    [GeneratedRegex(@"^(?=[^\n]*\b[A-Za-z0-9_\-]*(?:key|secret|token|passw|credential|auth)[A-Za-z0-9_\-]*\b)[^\n]*?(?<![A-Za-z0-9+/=_\-])(?<value>(?=[A-Za-z0-9+/=_\-]*[0-9])(?=[A-Za-z0-9+/=_\-]*[A-Za-z])(?![0-9a-f]{40}(?![A-Za-z0-9])|[0-9a-f]{64}(?![A-Za-z0-9]))[A-Za-z0-9+/=_\-]{24,})", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OpaqueNearKeyword();
}
