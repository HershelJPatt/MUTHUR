using System.Text.Json;

namespace Muthur.Launch;

public sealed record CapabilityProbeStep(string Key, string Command, string Receipt);

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
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)test.txt" Lines="{{nonce}}" Overwrite="true" />
          </Target>
        </Project>
        """;

    public static IReadOnlyList<CapabilityProbeStep> Steps(string fixture, string nonce)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string File(string name) => Quote(Path.Combine(fixture, name));
        CapabilityProbeStep Step(string key, string command, string exit) => new(key, command,
            $"@{{ nonce = '{nonce}'; exitCode = {exit} }} | ConvertTo-Json -Compress | Set-Content -LiteralPath {File(key + ".receipt.json")}");
        return [
            Step("shell", $"Set-Content -LiteralPath {File("shell.txt")} -Value '{nonce}'", "$(if ($?) { 0 } else { 1 })"),
            Step("worktree-base", $"git rev-parse HEAD > {File("head.txt")}", "$LASTEXITCODE"),
            Step("build", $"dotnet build {File("fixture.proj")} --no-restore", "$LASTEXITCODE"),
            Step("test", $"dotnet test {File("fixture.proj")} --no-restore", "$LASTEXITCODE"),
            Step("commit", $"git -C {File("commit")} add -- nonce.txt\nif ($LASTEXITCODE -eq 0) {{ git -C {File("commit")} -c core.hooksPath={File("no-hooks")} -c user.name=CapabilityFixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --quiet -m 'capability fixture' }}", "$LASTEXITCODE"),
        ];
    }

    public static string Prompt(IReadOnlyList<CapabilityProbeStep> steps) =>
        "Execute each Command exactly once, directly as a standalone PowerShell shell-tool command. " +
        "Do not wrap these commands in a script or substitute msbuild. Immediately after each Command, execute its Receipt in the same shell. " +
        "For a tool denial, record exitCode 126 for that step if writing the receipt is permitted, then continue the other steps. " +
        "Never edit fixture inputs, invent outputs, or request more permissions. " + Limitation + "\n" +
        JsonSerializer.Serialize(steps, CapabilityJsonContext.Default.IReadOnlyListCapabilityProbeStep);
}
