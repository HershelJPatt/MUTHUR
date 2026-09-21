using System.Text.Json;
using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Cli.Infrastructure;

internal sealed class ProbeAdmissionClient(HubClient hub) : IProbeAdmissionClient
{
    public async Task<ProbeAdmissionDto> AdmitAsync(ProbeAdmissionRequest request, CancellationToken ct = default)
    {
        var result = await hub.PostAsync(Routes.ProbeAdmit, request, MuthurJsonContext.Default.ProbeAdmissionRequest, ct);
        if (!result.IsSuccess) throw new InvalidOperationException($"Probe admission refused ({result.Status}): {result.Body}");
        return JsonSerializer.Deserialize(result.Body, MuthurJsonContext.Default.ProbeAdmissionDto)
            ?? throw new InvalidOperationException("The hub returned no probe admission.");
    }

    public async Task ReleaseAsync(ProbeReleaseRequest request, CancellationToken ct = default)
    {
        var result = await hub.PostAsync(Routes.ProbeRelease, request, MuthurJsonContext.Default.ProbeReleaseRequest, ct);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Probe release refused for {request.ReservationId} ({result.Status}): {result.Body}");
    }
}
