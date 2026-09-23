namespace Muthur.Launch;

/// <summary>
/// A harness that spends no tokens: the session is <c>muthur sim agent</c>, a scripted client of the hub that
/// reads the same prompt a model would and drives the task through the same API. Everything else — conductor,
/// leases, validation, integration, landing — is the real product. Its job is to show the flow on a scratch hub
/// and to be the one end-to-end fixture of the loop; it is never staffed on a hub that holds real work, and the
/// agent itself refuses a home or URL that could be one. The catalog model travels with the invocation so one
/// candidate can play an account out of quota, and a non-zero exit that reads like a limit is treated as one,
/// exactly as the real adapters treat their CLIs.
/// </summary>
public sealed class SimAdapter : IHarnessAdapter
{
    public string Name => "sim";

    public string? WorkerNote => null;

    private static string ReportFile(WorkerRequest request) => Path.Combine(request.ScratchDirectory, "sim-report.txt");

    public HarnessInvocation Build(WorkerRequest request) =>
        new("muthur", ["sim", "agent", "--report", ReportFile(request), "--cd", request.WorkingDirectory, "--model", request.Model], request.Prompt);

    public WorkerOutcome Interpret(WorkerRequest request, ProcessResult result)
    {
        var file = ReportFile(request);
        var report = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        if (result.Ok && report.StartsWith("STATUS: done", StringComparison.Ordinal)) return new WorkerOutcome(true, report, RateLimited: false);

        var text = result.Ok ? (report.Length > 0 ? report : "The scripted session wrote no report.") : $"Process exited with code {result.ExitCode}: {result.Message}";
        if (!result.Ok && report.Length > 0) text += "\n\nFinal report from this attempt:\n" + report;
        return new WorkerOutcome(false, text, RateLimited: !result.Ok && Harnesses.LooksRateLimited(result.StdErr + result.StdOut));
    }
}
