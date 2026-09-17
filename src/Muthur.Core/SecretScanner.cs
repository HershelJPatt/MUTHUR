using System.Text;
using System.Text.RegularExpressions;

namespace Muthur.Core;

public enum SecretSeverity
{
    /// <summary>Looks like it could be a credential. The text may be drafted, but only the founder can let it leave.</summary>
    Suspect,
    /// <summary>A recognizable credential. The text is refused and never stored.</summary>
    Block,
}

public sealed record SecretFinding(string Kind, string Excerpt, SecretSeverity Severity = SecretSeverity.Block);

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

    private static readonly Regex WeakAssignmentPattern = WeakAssignment();

    /// <summary>Shapes that depend on the words around them; only meaningful on the text as written.</summary>
    private static readonly (string Kind, Regex Pattern)[] ContextPatterns =
    [
        ("authorization-header", AuthorizationHeader()),
        ("connection-string-secret", ConnectionStringSecret()),
        ("credential-assignment", StrongAssignment()),
        ("credential-assignment", WeakAssignmentPattern),
        ("credential-in-prose", Prose()),
        ("possible-passphrase", ProseWords()),
        ("possible-passphrase", QuotedPassphrase()),
        ("credential-in-markup", XmlElement()),
        ("credential-in-markup", XmlAttributePair()),
        ("command-line-credential", CommandLine()),
        ("url-credentials", UrlCredentials()),
        ("opaque-value-near-keyword", OpaqueNearKeyword()),
    ];

    /// <summary>
    /// Two verdicts, because one cannot be both safe and usable: recognizable credentials are <see cref="SecretSeverity.Block"/>;
    /// anything that merely looks like one near password-class words is <see cref="SecretSeverity.Suspect"/>, which costs
    /// the founder a look instead of costing the organization a leak or the agents a useless gate.
    /// </summary>
    public static IReadOnlyList<SecretFinding> Scan(string text)
    {
        var findings = new List<SecretFinding>();
        var visible = RemoveInvisible(text);
        foreach (var (kind, pattern) in TokenPatterns)
            foreach (Match match in pattern.Matches(visible))
                findings.Add(new SecretFinding(kind, Mask(match.Value)));

        foreach (var (kind, pattern) in ContextPatterns)
        {
            foreach (Match match in pattern.Matches(visible))
            {
                var group = match.Groups["value"];
                var value = group.Success ? CleanValue(group.Value) : null;
                if (value is not null && IsNotACredential(value)) continue;
                // secretName: sql-connection, PASSWORD_FILE=…, passwordMinLength: the name says the value is a label, a location or a number
                if (value is not null && NamesSomethingElse().IsMatch(visible[match.Index..group.Index])) continue;

                // "key", "token", "credentials" name plenty of things that are not secrets (S3 object keys, cache keys,
                // idempotency keys, key file paths, key names). For that vocabulary only an opaque value counts.
                var weakVocabulary = ReferenceEquals(pattern, WeakAssignmentPattern) || kind == "opaque-value-near-keyword";
                if (weakVocabulary && value is not null)
                {
                    // after an explicit assignment a hex or base64 run IS the value, so only structure (not shape) excuses it
                    var name = visible[match.Index..group.Index];
                    if (value.All(char.IsAsciiLetter)) continue;
                    if (kind == "opaque-value-near-keyword" && IsReference(WholeToken(visible, group))) continue; // a long URL or path segment is not a secret
                    if (NamesSomethingElse().IsMatch(name)) continue; // KEY_PATH=, key_name =, KEY_SIZE=

                    var structured = kind == "opaque-value-near-keyword" ? IsStructured(value) : HasWordStructure(value);
                    if (structured)
                    {
                        // A bare "key" names many harmless things (object keys, cache keys, idempotency keys): structure excuses it.
                        // A credential-grade name (API_KEY, *_TOKEN, X-Api-Key, credentials) does not get that benefit: some services'
                        // tokens really are UUIDs, and "staging-admin-2026" is a password. Not blocked — but the founder looks.
                        if (kind != "opaque-value-near-keyword" && CredentialGradeName().IsMatch(name) && value.Any(char.IsAsciiDigit))
                            findings.Add(new SecretFinding(kind, name + new string('*', 6) + $" ({group.Length} chars)", SecretSeverity.Suspect));
                        continue;
                    }
                }

                var suspectOnly = kind is "opaque-value-near-keyword" or "possible-passphrase";
                var severity = suspectOnly || (value is not null && !LooksLikeASecretValue(value)) ? SecretSeverity.Suspect : SecretSeverity.Block;
                // the name stays readable so the author can find it; the value never does
                var excerpt = suspectOnly || !group.Success ? Mask(value ?? match.Value) : visible[match.Index..group.Index] + new string('*', 6) + $" ({group.Length} chars)";
                findings.Add(new SecretFinding(kind, excerpt, severity));
            }
        }

        var joined = Join(visible);
        foreach (var (kind, pattern) in TokenPatterns)
            foreach (Match match in pattern.Matches(joined))
                if (!findings.Any(f => f.Kind == kind))
                    findings.Add(new SecretFinding(kind + " (split across whitespace or markup)", Mask(match.Value)));

        findings.AddRange(SuspectsNearPasswordWords(visible, findings));
        return findings;
    }

    /// <summary>The whitespace-delimited token a capture sits in.</summary>
    private static string WholeToken(string text, Group capture)
    {
        var start = capture.Index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = capture.Index + capture.Length;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return text[start..end].TrimStart('(', '<', '[', '"', '\'');
    }

    /// <summary>The whole value is a stand-in: $VAR, ${VAR}, &lt;your key&gt;, {{ secrets.X }}, %VAR%, ****.</summary>
    private static bool IsPlaceholder(string value) => PlaceholderValue().IsMatch(value);

    /// <summary>The value points somewhere else instead of being the secret: a URL or a path.</summary>
    private static bool IsReference(string value) =>
        value.Contains("://", StringComparison.Ordinal) || value.StartsWith('/') || value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith('~') ||
        value.StartsWith('\\') || (value.Length > 2 && value[1] == ':' && value[2] is '\\' or '/');

    /// <summary>
    /// A real secret has a digit or a symbol in it. A bare word (keyvault), a dotted identifier (settings.DB_PASSWORD) or
    /// a call (TimeSpan.FromMinutes) after a password-like name is more likely configuration — suspect, not blocked.
    /// </summary>
    private static bool LooksLikeASecretValue(string value) =>
        value.Any(char.IsDigit) || value.Any(ch => "!@#$%^&*+/=~?".Contains(ch));

    /// <summary>
    /// After a password-like name, a code reference (settings.DB_PASSWORD, TimeSpan.FromMinutes) or one short lowercase
    /// word (keyvault, required, true) is configuration, not a credential.
    /// </summary>
    private static bool IsConfigurationWord(string value) =>
        CodeReference().IsMatch(value) || (value.All(char.IsAsciiLetter) && (value.Length <= 12 || value.All(char.IsAsciiLetterLower)));

    /// <summary>A name made of words and numbers: ACME_PASSWORD_MIN_LENGTH, 20240917_AddPasswordHashColumn, settings.Value.</summary>
    private static bool IsIdentifier(string value)
    {
        var segments = value.Split('_', '.');
        return segments.Length >= 2 && segments.All(s => s.Length > 0 && (s.All(char.IsAsciiLetter) || s.All(char.IsAsciiDigit)));
    }

    /// <summary>
    /// A value with visible structure — a file name, or words and numbers joined by separators (reports/2026/q3/summary.pdf,
    /// orders:by-customer:v3, deploy-keypair-2026, createdAt#orderId, build01-win-x64) — or a known non-secret shape (UUID, hex id, version).
    /// Opaque secrets have no such structure: they are one run of mixed characters.
    /// </summary>
    private static bool IsStructured(string value)
    {
        if (NotASecretShape().IsMatch(value)) return true;
        return HasWordStructure(value);
    }

    private static bool HasWordStructure(string value)
    {
        if (FileName().IsMatch(value) || Uuid().IsMatch(value)) return true;
        var segments = value.Split(['-', '_', '.', ':', '/', '#', '\\', '+', '~', '@', '$', '{', '}'], StringSplitOptions.RemoveEmptyEntries);
        // every segment must be a word, a number, or a word with a number on it (x64, v3, build01): one opaque segment
        // ("gsk_aB3dE5…", "prod-Sup3rS3cret9", "aZ3k-Q9mX-2vB7") means the value is a token wearing a prefix or dashes
        return segments.Length >= 2 && segments.All(s => s.All(char.IsAsciiLetter) || s.All(char.IsAsciiDigit) || WordWithNumber().IsMatch(s));
    }

    private static string CleanValue(string raw) => raw.Trim('"', '\'', '`').TrimStart('!', '(').TrimEnd(')', '.', ',', ';', ']', '}');

    private static bool IsNotACredential(string value) =>
        IsPlaceholder(value) || IsReference(value) || IsConfigurationWord(value) || IsIdentifier(value);

    /// <summary>
    /// The catch-all: a standalone mixed-class token (8+ characters, a digit or symbol in it, not a URL, path, hash, id,
    /// version or number) on a line that talks about passwords, logins or credentials — or right below such a line,
    /// which is how tables, YAML name/value pairs and "password:\n  value" look.
    /// </summary>
    private static IEnumerable<SecretFinding> SuspectsNearPasswordWords(string text, List<SecretFinding> already)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // this line and the two above it: a table row sits two lines below its header
            var context = string.Join('\n', lines[Math.Max(0, i - 2)..(i + 1)]);
            var nearPasswordWord = PasswordClassWord().IsMatch(context);
            // right next to the word itself ("password = Sup3r S3cret") even a short mixed token is worth a look
            var minimum = StrongPasswordWord().IsMatch(lines[i]) ? 5 : 8;
            foreach (Match token in StandaloneToken().Matches(lines[i]))
            {
                var value = token.Value.Trim('"', '\'', '`', '*', '_', '(', ')', ',', ';', '|', '<', '>', '.', '[', ']', '{', '}');
                // -pSECRET, -wSECRET, /p:SECRET: a password flag with its value glued on needs no other context
                var glued = GluedPasswordFlag().Match(value);
                if (glued.Success) value = glued.Groups["value"].Value;
                else if (!nearPasswordWord || IsReference(value) || value.StartsWith('@')) continue; // URLs, paths, @mentions and @Attributes

                // user:secret, key=secret: judge the part that would be the secret
                var cut = value.LastIndexOfAny([':', '=']);
                if (cut >= 0 && cut < value.Length - 1) value = value[(cut + 1)..];
                value = CleanValue(value);
                if (value.Length < minimum || value.StartsWith('@') || value.Contains('(') || IsNotACredential(value)) continue;
                if (value.Contains('/') && value[..value.IndexOf('/')].All(char.IsAsciiLetterLower)) continue; // task/T-9-x, src/app/main.cs
                // "api_token:" alone on the line above: whatever stands below it is its value, even if it looks like a hash
                var namedAbove = i > 0 && DanglingSecretName().IsMatch(lines[i - 1]);
                // some services' API keys and tokens are UUIDs; next to such a word a UUID is worth the founder's look
                // (a correlation id next to "password error", or an Idempotency-Key example, is not)
                var before = lines[i][..Math.Max(0, lines[i].IndexOf(value, StringComparison.Ordinal))];
                var near = (i > 0 ? lines[i - 1] + " " : "") + (before.Length > 45 ? before[^45..] : before);
                var uuidAsCredential = Uuid().IsMatch(value) && CredentialGradeName().IsMatch(near)
                    && !NamesSomethingElse().IsMatch(before); // "tenantId: <uuid>" is an id whatever else the line mentions
                if (IsStructured(value) && !uuidAsCredential && !(namedAbove && value.Length >= 16)) continue;
                // a digit among the letters, or a symbol in a mixed-case word: "Sup3rS3cret", "Tr0ub4dor&3", "Correct!Horse" — not "-nuget-$"
                var mixedCase = value.Any(char.IsAsciiLetterUpper) && value.Any(char.IsAsciiLetterLower);
                if (!value.Any(char.IsLetter) || !(value.Any(char.IsDigit) || (mixedCase && value.Any(ch => "!@#$%^&*+=~?".Contains(ch))))) continue;
                var masked = Mask(value);
                if (already.Any(f => f.Excerpt == masked)) continue;
                yield return new SecretFinding("possible-credential-near-password-word", masked, SecretSeverity.Suspect);
            }
        }
    }

    // A shell variable is a placeholder when it reads like one: $TOKEN, ${DB_PASSWORD}, $password — not $up3rS3cret9
    [GeneratedRegex(@"^(?:\$\{?(?:[A-Z_][A-Z0-9_.]*|[A-Za-z_.]+)\}?|<[^<>0-9]{1,80}>|\{\{[^{}]{1,80}\}\}|\$\{\{[^{}]{1,80}\}\}|%[A-Za-z_][A-Za-z0-9_]*%|\*{3,}|[xX]{3,}|\.{3,}|…)$")]
    private static partial Regex PlaceholderValue();

    // settings.DB_PASSWORD · TimeSpan.FromMinutes(30) · Configuration["Smtp:Password"] · os.environ
    [GeneratedRegex(@"^@?[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+(?:\(.*)?$|^@?[A-Za-z_][A-Za-z0-9_.]*[\[(].*$")]
    private static partial Regex CodeReference();

    // No word boundaries around token/pwd/secret/…: GITHUB_TOKEN, DB_PWD and AccessToken are names too ("_" and letters are word characters).
    [GeneratedRegex(@"pass(?:word|wd|phrase|code|wort)|(?<![A-Za-z])pass(?![A-Za-z])|_pass(?![A-Za-z])|pwd|(?<![A-Za-z])pw(?![A-Za-z])|secret|credential|creds|(?<![A-Za-z])log\s?in(?![A-Za-z])|token|api[ _\-]?key|(?<![A-Za-z])key(?![A-Za-z])|_key(?![A-Za-z])|(?-i:(?<=[a-z])Key(?![a-z]))|(?<![A-Za-z])auth(?![A-Za-z])|(?<![A-Za-z0-9])-[pPaw](?![A-Za-z0-9])|--password|/p:|IDENTIFIED\s+BY|SecureString|NetworkCredential|/user:", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordClassWord();

    [GeneratedRegex(@"pass(?:word|wd|phrase|code|wort)|pwd|(?<![A-Za-z])pw(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex StrongPasswordWord();

    // the assignment's name says the value is a location, a label or a number, not the secret: SSH_KEY_PATH=, key_name =, KEY_SIZE=, "KeyPath":
    [GeneratedRegex(@"(?:path|file|name|names|id|ids|size|length|bits|days|hours|minutes|seconds|ttl|url|uri|dir|directory|type|count|version|algorithm|alg|format|prefix|suffix|header|field|column|index|vault|store|provider|rotation[A-Za-z_]*)[""']?[ \t]*(?:=>|:=|[:=：]|\t)[ \t]*[""']?$", RegexOptions.IgnoreCase)]
    private static partial Regex NamesSomethingElse();

    [GeneratedRegex(@"^[^\s]*[A-Za-z0-9_\-]\.[A-Za-z][A-Za-z0-9]{0,5}$")]
    private static partial Regex FileName();

    // names that are credentials by themselves, unlike a bare "key"
    [GeneratedRegex(@"token|api[ _\-]?key|access[_\-]?key|private[_\-]?key|secret|credential|(?<![A-Za-z])auth|_pat(?![A-Za-z])|license", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialGradeName();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Uuid();

    // build01, x64, v3, 2026q3, ed25519
    [GeneratedRegex(@"^(?:[A-Za-z]+\d{1,6}|\d{1,6}[A-Za-z]{1,3})$")]
    private static partial Regex WordWithNumber();

    // a line that is nothing but a sensitive name, optionally with a list dash, quotes and a trailing : or =
    [GeneratedRegex(@"^[\s\-]*[""']?[A-Za-z0-9_.\-]*(?:pass|pwd|secret|token|credential|key)[A-Za-z0-9_.\-]*[""']?\s*[:=]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DanglingSecretName();

    [GeneratedRegex(@"^(?:-[pPw]|/[pP]:)(?<value>.{6,})$")]
    private static partial Regex GluedPasswordFlag();

    // split on whitespace and on the punctuation that separates fields in JSON, CSV, tables and markup
    [GeneratedRegex(@"[^\s,""'<>|(){}\[\]]+")]
    private static partial Regex StandaloneToken();

    // things that are long and mixed but are not secrets: hex ids and hashes, UUIDs, versions, numbers with units, times and dates,
    // e-mail addresses, @mentions, issue refs, key=value pairs and flags (their value part is scanned by the other rules)
    [GeneratedRegex(@"^(?:[0-9a-fA-F]{7,}|0x[0-9a-fA-F]+|[0-9a-fA-F]{8}-[0-9a-fA-F\-]{27}|(?:Ctrl|Alt|Shift|Cmd|Win|Meta|Option|Fn)(?:\+\w+)+|SHA256:[A-Za-z0-9+/=]+|AAAA[A-Za-z0-9+/=]{20,}|[^@\s]+@[A-Za-z0-9.\-]+|v?\d+(?:\.\d+)+[\w.\-]*|\d[\d.,:/\-]*[A-Za-z%]{0,4}|[^@\s]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|@[\w\-]+|#\d+|[A-Za-z]+-\d+|--?[A-Za-z][\w\-]*(?:=.*)?|[A-Za-z_][\w.\-]*[=:].*|[A-Za-z]+\d{0,4}|(?:[A-Za-z]+[./\\_\-])+[A-Za-z]+\d{0,4})$")]
    private static partial Regex NotASecretShape();

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
    [GeneratedRegex(@"(?:sk-(?:ant-|proj-|live-|test-)?[A-Za-z0-9_\-]{20,}|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}|whsec_[A-Za-z0-9]{20,}|xai-[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{30,}|ya29\.[A-Za-z0-9_\-]{20,}|GOCSPX-[A-Za-z0-9_\-]{20,}|npm_[A-Za-z0-9]{30,}|glpat-[A-Za-z0-9_\-]{20,}|pypi-[A-Za-z0-9_\-]{30,}|dop_v1_[a-f0-9]{40,}|shp(?:at|ss|ca)_[a-fA-F0-9]{30,}|hf_[A-Za-z0-9]{30,}|hvs\.[A-Za-z0-9_\-]{20,}|lin_api_[A-Za-z0-9]{20,}|sq0(?:atp|csp)-[A-Za-z0-9_\-]{20,}|NRAK-[A-Z0-9]{20,}|key-[0-9a-f]{32}|dckr_pat_[A-Za-z0-9_\-]{20,}|ATATT3[A-Za-z0-9_\-=]{20,}|dapi[0-9a-f]{32}|glrt-[A-Za-z0-9_\-]{16,}|glsa_[A-Za-z0-9_]{20,}|PMAK-[A-Za-z0-9\-]{20,}|[A-Za-z0-9]{14}\.atlasv1\.[A-Za-z0-9]{20,}|dp\.pt\.[A-Za-z0-9]{20,}|secret_[A-Za-z0-9]{40,}|ntn_[A-Za-z0-9]{40,}|figd_[A-Za-z0-9_\-]{20,}|pul-[0-9a-f]{40}|pscale_(?:pw|tkn)_[A-Za-z0-9_\-.]{20,}|sk\.eyJ[A-Za-z0-9_\-.]{20,})")]
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
    [GeneratedRegex(@"[""']?(?<![A-Za-z])[A-Za-z0-9_.\-]*?(?:token|api[_\-]?key|access[_\-]?key|private[_\-]?key|encryption[_\-]?key|signing[_\-]?key|secret[_\-]?key|credentials?|_auth|auth[_\-]?key|_pat|(?<![A-Za-z])key|_key|(?-i:(?<=[a-z])Key))[A-Za-z0-9_.\-]*[""']?[ \t]*(?:=>|:=|[:=：]|\t)[ \t]*[""']?(?<value>[^\s""',;}<]{16,})", RegexOptions.IgnoreCase)]
    private static partial Regex WeakAssignment();

    // "the password is hunter2hunter2", netrc "login bob password hunter2x" — a value with a digit in it, so "password is required" passes
    [GeneratedRegex(@"\bpass(?:word|phrase|code|wd)?\s+(?:(?:is|was|to|:|=)\s+)?(?<value>(?=\S*\d)[^\s.,;]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex Prose();

    // "the password is CorrectHorseBatteryStaple": letters only, so it may be an ordinary sentence ("the password is incorrect") — suspect at most
    [GeneratedRegex(@"\bpass(?:word|phrase|code)?\s+(?:is|was)\s+(?<value>[A-Za-z]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex ProseWords();

    // password: 'My dog Rex 2019' — a quoted value with spaces after a password-like name
    [GeneratedRegex(@"pass(?:word|wd|phrase|code)?[""']?\s*[:=]\s*[""'](?<value>(?=[^""'\n]*\s)(?=[^""'\n]*\d)[^""'\n]{6,})[""']", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedPassphrase();

    [GeneratedRegex(@"<(?<tag>[A-Za-z0-9_.\-]*(?:pass|pwd|secret|token|apikey|api_key)[A-Za-z0-9_.\-]*)>\s*(?<value>[^<\s]{6,})\s*</", RegexOptions.IgnoreCase)]
    private static partial Regex XmlElement();

    // <add key="SmtpPassword" value="…"/>
    [GeneratedRegex(@"[""'][A-Za-z0-9_.\-]*(?:pass|pwd|secret|token|apikey|api_key|(?-i:(?<=[a-z])Key)|_key)[A-Za-z0-9_.\-]*[""']\s+value\s*=\s*[""'](?<value>[^""']{6,})", RegexOptions.IgnoreCase)]
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
