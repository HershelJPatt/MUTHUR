using System.Text.RegularExpressions;

namespace Muthur.Core;

public sealed record SecretFinding(string Kind, string Excerpt);

/// <summary>
/// Looks for credentials in text that is about to leave the machine. Deliberately biased toward false
/// positives: a blocked message costs a rewrite, a leaked key costs a rotation (or worse).
/// </summary>
public static partial class SecretScanner
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("private-key", PrivateKey()),
        ("aws-access-key", AwsAccessKey()),
        ("github-token", GitHubToken()),
        ("api-key", PrefixedApiKey()),
        ("slack-token", SlackToken()),
        ("jwt", Jwt()),
        ("bearer-token", Bearer()),
        ("connection-string-secret", ConnectionStringSecret()),
        ("sas-signature", SasSignature()),
        ("credential-assignment", CredentialAssignment()),
        ("webhook-url", WebhookUrl()),
    ];

    public static IReadOnlyList<SecretFinding> Scan(string text)
    {
        var findings = new List<SecretFinding>();
        foreach (var (kind, pattern) in Patterns)
            foreach (Match match in pattern.Matches(text))
                findings.Add(new SecretFinding(kind, Mask(match.Value)));
        return findings;
    }

    /// <summary>Enough to find it in the draft, never enough to use it.</summary>
    private static string Mask(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= 12 ? new string('*', value.Length) : $"{value[..6]}…{new string('*', 6)} ({value.Length} chars)";
    }

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY(?: BLOCK)?-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})\b")]
    private static partial Regex GitHubToken();

    // sk-…, sk-ant-…, sk-proj-…, xai-…, AIza… and similar vendor-prefixed keys
    [GeneratedRegex(@"\b(?:sk-(?:ant-|proj-|live-|test-)?[A-Za-z0-9_\-]{20,}|xai-[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{30,})\b")]
    private static partial Regex PrefixedApiKey();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}\b")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9_\-\.=]{20,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(?:Password|Pwd|AccountKey|SharedAccessKey|SharedSecret)\s*=\s*[^;\s""']{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"[?&]sig=[A-Za-z0-9%+/=]{20,}")]
    private static partial Regex SasSignature();

    // password: hunter22, api_key = "…", client_secret='…', token: …   (a value of 8+ non-space chars)
    [GeneratedRegex(@"\b(?:password|passwd|secret|api[_\-]?key|client[_\-]?secret|access[_\-]?token|auth[_\-]?token)\b\s*[:=]\s*[""']?[^\s""']{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignment();

    [GeneratedRegex(@"https://(?:discord(?:app)?\.com/api/webhooks|hooks\.slack\.com/services)/[A-Za-z0-9/_\-]{20,}")]
    private static partial Regex WebhookUrl();
}
