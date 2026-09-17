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
        Assert.True(SecretScanner.Scan(Decode(text)).Count > 0, $"block.txt line {line} was not detected: {text}");

    [Theory]
    [MemberData(nameof(Pass))]
    public void Ordinary_text_passes(int line, string text)
    {
        var findings = SecretScanner.Scan(Decode(text));
        Assert.True(findings.Count == 0, $"pass.txt line {line} was refused as {string.Join(", ", findings.Select(f => f.Kind))}: {text}");
    }

    [Fact]
    public void Findings_never_repeat_the_secret()
    {
        var secret = "gh" + "p_abcdefghijklmnopqrstuvwxyzABCDEF0123";
        var finding = Assert.Single(SecretScanner.Scan($"use {secret} please"));
        Assert.DoesNotContain(secret, finding.Excerpt);
        Assert.StartsWith("ghp_ab", finding.Excerpt);
    }
}
