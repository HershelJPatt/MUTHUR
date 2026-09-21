using System.Text.Json;
using Muthur.Contracts;
using Muthur.Launch;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

public interface IIntegrationSessionLauncher
{
    Task StartAsync(string task, CancellationToken ct = default);
}

/// <summary>Launches only the installed deterministic CLI with a narrowly scoped runner identity.</summary>
public sealed class IntegrationSessionLauncher(AgentService agents, IntegrationService integration, MuthurOptions options, IProcessRunner processes)
    : IIntegrationSessionLauncher
{
    public async Task StartAsync(string task, CancellationToken ct = default)
    {
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", OperatingSystem.IsWindows() ? "muthur.exe" : "muthur"));
        if (!File.Exists(cli)) throw new ValidatorLaunchException("integration_cli_missing: install the bundled CLI before enabling deterministic integration staffing.");
        var registered = await agents.RegisterIntegrationRunnerAsync(new("integration-" + Guid.NewGuid().ToString("N"), "deterministic", "none", "utility"), ct);
        var caller = new Caller(CallerKind.Agent, registered.Agent.Id, registered.Agent.Name, "deterministic/none", true);
        var assignment = await integration.AssignRunnerAsync(caller, task, ct);
        var folder = Path.Combine(options.DataDir, "integration", assignment.Candidate.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "assignment.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(assignment, MuthurJsonContext.Default.IntegrationAssignmentDto), ct);
        var environment = new Dictionary<string, string>
        {
            [MuthurEnvironment.HomeVariable] = options.DataDir, [MuthurEnvironment.UrlVariable] = options.Url,
            [MuthurEnvironment.TokenVariable] = registered.Token, ["MUTHUR_AGENT"] = registered.Agent.Name,
        };
        var result = await processes.RunAsync(cli, ["integration", "run", task, "--assignment-file", file], assignment.Candidate.RepositoryPath,
            timeout: TimeSpan.FromSeconds(assignment.SessionTimeoutSeconds + 60), ct: ct, environment: environment);
        await File.WriteAllTextAsync(Path.Combine(folder, "runner.log"), result.StdOut + "\n" + result.StdErr, CancellationToken.None);
        if (!result.Ok) throw new ValidatorSessionException("Integration exited without a passing report: " + result.Message);
    }
}
