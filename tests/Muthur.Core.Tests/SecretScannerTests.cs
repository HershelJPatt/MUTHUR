using Muthur.Core;

namespace Muthur.Core.Tests;

/// <summary>
/// The scanner's contract is the corpus: SecretCorpus/block.txt must all be refused, SecretCorpus/pass.txt must all pass.
/// A credential shape that gets past the gate is fixed by adding a line there first.
/// </summary>
public sealed class SecretScannerTests
{
    public static TheoryData<int, string> Block => Load("block.txt");
    public static TheoryData<int, string> Pass => Load("pass.txt");
    public static TheoryData<int, string> Flag => Load("flag.txt");
    public static TheoryData<int, string> Clean => Load("clean.txt");

    private static TheoryData<int, string> Load(string file)
    {
        var data = new TheoryData<int, string>();
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "SecretCorpus", file));
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0 || lines[i].StartsWith('#')) continue;
            data.Add(i + 1, lines[i]);
        }
        return data;
    }

    /// <summary>~ only exists so the corpus file never contains a literal token; the escapes make one-line cases of multi-line text.</summary>
    private static string Decode(string line) => line
        .Replace("~", "")
        .Replace("\\u200B", "​")
        .Replace("\\n", "\n")
        .Replace("\\t", "\t")
        .Replace("\\\\", "\\");

    [Theory]
    [MemberData(nameof(Block))]
    public void Credentials_are_refused(int line, string text) =>
        Assert.True(SecretScanner.Scan(Decode(text)).Any(f => f.Severity == SecretSeverity.Block), $"block.txt line {line} was not blocked: {text}");

    [Theory]
    [MemberData(nameof(Flag))]
    public void Possible_credentials_are_at_least_flagged_for_the_founder(int line, string text) =>
        Assert.True(SecretScanner.Scan(Decode(text)).Count > 0, $"flag.txt line {line} was neither flagged nor blocked: {text}");

    [Theory]
    [MemberData(nameof(Clean))]
    public void Everyday_messages_raise_nothing(int line, string text)
    {
        var findings = SecretScanner.Scan(Decode(text));
        Assert.True(findings.Count == 0, $"clean.txt line {line} raised {string.Join(", ", findings.Select(f => f.Kind + "/" + f.Severity))}: {text}");
    }

    [Theory]
    [MemberData(nameof(Pass))]
    public void Ordinary_text_passes(int line, string text)
    {
        var findings = SecretScanner.Scan(Decode(text)).Where(f => f.Severity == SecretSeverity.Block).ToList();
        Assert.True(findings.Count == 0, $"pass.txt line {line} was refused as {string.Join(", ", findings.Select(f => f.Kind))}: {text}");
    }

    /// <summary>Where people write a password down. {0} is the password.</summary>
    private static readonly string[] Carriers =
    [
        "Temporary password for the new hire: {0}",
        "The password for the staging admin is now {0}",
        "the pw is {0}",
        "docker login registry.acme.io -u deploy -p {0}",
        "| user | Password |\n|---|---|\n| admin | {0} |",
        "user,password\nadmin,{0}",
        "CREATE USER app IDENTIFIED BY '{0}';",
        "- name: DB_PASSWORD\n  value: {0}",
        "Login for the staging box: admin / {0}",
        "net user deploy {0} /add",
    ];

    /// <summary>How people build one: a word or two, a number, maybe a symbol — including the password word itself.</summary>
    private static readonly string[] HumanPasswords =
    [
        "Winter_2026", "W1nter2026", "AdminWinter1", "TempWinter2026", "Admin.Winter.2026", "Acme-Winter-2026", "SuperWinter1",
        "Password1", "Password123!", "Password2026", "Secret_2026", "Secret123", "Token_2026",
        "Passwort1!", "Kennwort2026", "Login2026", "Creds_2026", "ApiKey123!", "Auth2026!", "Pwd12345", "Passwd2026", "Credentials1",
        "Token12345", "Secret1!", "Passcode2026", "Login123",
        "MyPassword1", "NewPassword2026", "AcmePassword1!", "Password@Acme1", "TempPass2026", "AdminSecret99", "MySecret123", "TopSecret1",
        "AdminPassword1", "RootPass123", "AdminPass1", "TempPassword1", "TestPassword123", "Admin.Password.2026", "Acme-Secret-2026",
        "SuperSecret1", "Changeme.Password1", "Autumn.Secret.2026",
        "password123", "winter2026", "adminpass123", "WINTER2026", "PASSWORD123",
        "P4ssword1", "Passw0rd1", "P@ssword2026", "S3cret_2026", "T0ken_2026", "PassWord2026", "Pa55word1", "Secr3t2026", "L0gin2026",
    ];

    public static TheoryData<string, string> Matrix
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var carrier in Carriers)
                foreach (var password in HumanPasswords)
                    data.Add(carrier, password);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void A_human_password_is_caught_wherever_it_is_written(string carrier, string password) =>
        Assert.True(SecretScanner.Scan(string.Format(carrier, password)).Count > 0, $"neither flagged nor blocked: {string.Format(carrier, password)}");

    [Fact]
    public void Findings_never_repeat_the_secret()
    {
        var secret = "gh" + "p_abcdefghijklmnopqrstuvwxyzABCDEF0123";
        var finding = Assert.Single(SecretScanner.Scan($"use {secret} please"));
        Assert.DoesNotContain(secret, finding.Excerpt);
        Assert.StartsWith("ghp_ab", finding.Excerpt);
    }
}
