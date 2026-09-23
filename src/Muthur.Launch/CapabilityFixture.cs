using System.Text.Json;

namespace Muthur.Launch;

public sealed record CapabilityProbeStep(string Key, string Command, string Receipt);

/// <summary>The shell a harness's shell tool runs, which decides the form the probe's steps are written in.</summary>
public enum CapabilityShell { PowerShell, Posix }

/// <summary>Fixed offline inputs. The harness executes each command directly through its shell tool.</summary>
public static class CapabilityFixture
{
    public const string Limitation = "Fixture v2: explicit dotnet build/test --no-restore commands on a package-free MSBuild project. Proves SDK command execution and deterministic fixture assertions, not a framework test suite or arbitrary product correctness. No browser, connector, platform or native-agent evidence.";

    public static string Project(string nonce) => $$"""
        <Project>
          <Target Name="Build">
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)build.txt" Lines="{{nonce}}" Overwrite="true" />
          </Target>
          <Target Name="VSTest">
            <Error Condition="!Exists('$(MSBuildThisFileDirectory)build.txt')" Text="Build nonce output is missing." />
            <ReadLinesFromFile File="$(MSBuildThisFileDirectory)build.txt">
              <Output TaskParameter="Lines" ItemName="BuildNonce" />
            </ReadLinesFromFile>
            <Error Condition="'@(BuildNonce)' != '{{nonce}}'" Text="Build nonce output does not match." />
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)test.txt" Lines="{{nonce}}" Overwrite="true" />
          </Target>
        </Project>
        """;

    /// <summary>The command the deny-list step asks for: denied to every session, and harmless if a guard lets it through.</summary>
    public const string DeniedProbeCommand = "git push --dry-run";

    /// <param name="denyList">Add a step whose command the session's guard must refuse; its evidence is the refusal in the event stream.</param>
    /// <param name="shell">The shell the harness's shell tool runs; each step is written in that shell's own form.</param>
    public static IReadOnlyList<CapabilityProbeStep> Steps(string fixture, string nonce, bool denyList = false, CapabilityShell shell = CapabilityShell.PowerShell) =>
        shell == CapabilityShell.Posix ? PosixSteps(fixture, nonce, denyList) : PowerShellSteps(fixture, nonce, denyList);

    /// <summary>
    /// The same steps for sh. Paths use forward slashes, which Git Bash on Windows and the native tools it starts both
    /// read, and the receipt takes <c>$?</c> on the line straight after the command.
    /// </summary>
    private static IReadOnlyList<CapabilityProbeStep> PosixSteps(string fixture, string nonce, bool denyList)
    {
        static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
        string File(string name) => Quote(Path.Combine(fixture, name).Replace('\\', '/'));
        CapabilityProbeStep Step(string key, string command) => new(key, command,
            $"printf '{{\"nonce\":\"%s\",\"exitCode\":%s}}\\n' '{nonce}' \"$?\" > {File(key + ".receipt.json")}");
        IReadOnlyList<CapabilityProbeStep> guard = denyList ? [Step("deny-list", DeniedProbeCommand)] : [];
        return [
            Step("shell", $"printf '%s\\n' '{nonce}' > {File("shell.txt")}"),
            Step("worktree-base", $"git rev-parse HEAD > {File("head.txt")}"),
            Step("build", $"dotnet build {File("fixture.proj")} --no-restore"),
            Step("test", $"dotnet test {File("fixture.proj")} --no-restore"),
            Step("commit", $"git -C {File("commit")} add -- nonce.txt && git -C {File("commit")} -c core.hooksPath={File("no-hooks")} -c user.name=CapabilityFixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --quiet -m 'capability fixture'"),
            .. guard,
        ];
    }

    private static IReadOnlyList<CapabilityProbeStep> PowerShellSteps(string fixture, string nonce, bool denyList)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string File(string name) => Quote(Path.Combine(fixture, name));
        CapabilityProbeStep Step(string key, string command, string exit) => new(key, command,
            $"@{{ nonce = '{nonce}'; exitCode = {exit} }} | ConvertTo-Json -Compress | Set-Content -LiteralPath {File(key + ".receipt.json")}");
        IReadOnlyList<CapabilityProbeStep> guard = denyList ? [Step("deny-list", DeniedProbeCommand, "$LASTEXITCODE")] : [];
        return [
            Step("shell", $"Set-Content -LiteralPath {File("shell.txt")} -Value '{nonce}'", "$(if ($?) { 0 } else { 1 })"),
            Step("worktree-base", $"git rev-parse HEAD > {File("head.txt")}", "$LASTEXITCODE"),
            Step("build", $"dotnet build {File("fixture.proj")} --no-restore", "$LASTEXITCODE"),
            Step("test", $"dotnet test {File("fixture.proj")} --no-restore", "$LASTEXITCODE"),
            Step("commit", $"git -C {File("commit")} add -- nonce.txt\nif ($LASTEXITCODE -eq 0) {{ git -C {File("commit")} -c core.hooksPath={File("no-hooks")} -c user.name=CapabilityFixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --quiet -m 'capability fixture' }}", "$LASTEXITCODE"),
            .. guard,
        ];
    }

    public static string Prompt(IReadOnlyList<CapabilityProbeStep> steps, CapabilityShell shell = CapabilityShell.PowerShell) =>
        $"Execute each Command exactly once, directly as a standalone {(shell == CapabilityShell.Posix ? "bash" : "PowerShell")} shell-tool command. " +
        "For each step use ONE shell-tool invocation containing Command followed immediately by a newline and Receipt. " +
        "Keep the exact native command individually visible to the permission engine; do not wrap it in a script or substitute msbuild. " +
        $"The native exit code and receipt write MUST occur in that SAME invocation; a later fresh shell cannot read the prior {(shell == CapabilityShell.Posix ? "$?" : "LASTEXITCODE")}. " +
        "For a tool denial, record exitCode 126 for that step if writing the receipt is permitted, then continue the other steps. " +
        "Never edit fixture inputs, invent outputs, or request more permissions. " + Limitation + "\n" +
        JsonSerializer.Serialize(steps, CapabilityJsonContext.Default.IReadOnlyListCapabilityProbeStep);
}
