using System.Text.Json;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Infrastructure;

internal sealed class WorkerAdmissionClient(HubClient hub) : IWorkerAdmissionClient
{
    public async Task<WorkerAdmissionDto> AdmitAsync(WorkerAdmissionRequest request, CancellationToken ct = default)
    {
        var result = await hub.PostAsync(Routes.WorkerAdmit, request, MuthurJsonContext.Default.WorkerAdmissionRequest, ct);
        if (!result.IsSuccess) throw new InvalidOperationException($"Worker admission refused ({result.Status}): {result.Body}");
        return JsonSerializer.Deserialize(result.Body, MuthurJsonContext.Default.WorkerAdmissionDto)
            ?? throw new InvalidOperationException("The hub returned no worker admission.");
    }

    public async Task ReleaseAsync(WorkerReleaseRequest request, CancellationToken ct = default)
    {
        var result = await hub.PostAsync(Routes.WorkerRelease, request, MuthurJsonContext.Default.WorkerReleaseRequest, ct);
        if (!result.IsSuccess) throw new InvalidOperationException($"Worker release refused for {request.ReservationId} ({result.Status}): {result.Body}");
    }
}
