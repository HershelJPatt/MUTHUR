using System.Security.Cryptography;
using System.Text.Json;
using Muthur.Contracts;

namespace Muthur.Launch;

public interface IWorkerAdmissionClient
{
    Task<WorkerAdmissionDto> AdmitAsync(WorkerAdmissionRequest request, CancellationToken ct = default);
    Task ReleaseAsync(WorkerReleaseRequest request, CancellationToken ct = default);
}

public sealed record WorkerAdmissionContext(IWorkerAdmissionClient Client, string Task, string Tier, string Repository,
    string BaseCommit, string SpecBlob, string SpecPath, string? Unit, string? Parent, string Branch)
{
    public WorkerAdmissionRequest Bind(HarnessCandidate candidate, WorkerRequest request, string runId, TimeSpan timeout)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("task", Task); json.WriteString("tier", Tier);
            json.WriteString("harness", candidate.Harness); json.WriteString("model", candidate.Model); json.WriteString("account", candidate.Account);
            json.WriteString("repository", Path.GetFullPath(Repository)); json.WriteString("baseCommit", BaseCommit);
            json.WriteString("specBlob", SpecBlob); json.WriteString("specPath", SpecPath);
            json.WriteString("unit", Unit); json.WriteString("parent", Parent); json.WriteString("branch", Branch);
            json.WriteString("worktree", Path.GetFullPath(request.WorkingDirectory)); json.WriteNumber("timeoutTicks", timeout.Ticks);
            json.WriteString("prompt", request.Prompt);
            json.WriteStartArray("allowedCommands"); foreach (var command in request.AllowedCommands ?? []) json.WriteStringValue(command); json.WriteEndArray();
            json.WriteStartArray("capabilities"); foreach (var requirement in request.Capabilities?.Requirements ?? []) json.WriteStringValue(requirement); json.WriteEndArray();
            json.WriteEndObject();
        }
        return new(Task, Tier, candidate.Harness, candidate.Model, candidate.Account ?? "", runId,
            Convert.ToHexStringLower(SHA256.HashData(stream.ToArray())));
    }
}

/// <summary>Setup subprocesses use the same containment guarantee as the model itself.</summary>
public sealed class WorkerSetupRunner(IWorkerProcessRunner processes) : IProcessRunner
{
    public bool CleanupConfirmed { get; private set; } = true;
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? scrubEnvironment = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        CleanupConfirmed = false;
        var result = await processes.RunAsync(fileName, arguments, workingDirectory, stdin, timeout, ct, scrubEnvironment, environment ?? SessionWorkspace.GitEnvironment(workingDirectory));
        CleanupConfirmed = result.Cleanup != WorkerCleanup.CleanupUncertain;
        if (!CleanupConfirmed) throw new InvalidOperationException("Setup process cleanup is uncertain.");
        return result.Result;
    }
}
