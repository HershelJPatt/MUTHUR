using Muthur.Contracts;

namespace Muthur.Launch;

public interface IProbeAdmissionClient
{
    Task<ProbeAdmissionDto> AdmitAsync(ProbeAdmissionRequest request, CancellationToken ct = default);
    Task ReleaseAsync(ProbeReleaseRequest request, CancellationToken ct = default);
}

public sealed class ProbeCleanupUncertainException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Runs one admitted callback; it neither implements probes nor authorizes arbitrary commands.</summary>
public sealed class ProbeReservationRunner(IProbeAdmissionClient client)
{
    /// <summary>
    /// The callback must bound its execution and complete process-tree cleanup before returning or throwing an ordinary exception.
    /// If cleanup cannot be confirmed, throw ProbeCleanupUncertainException to retain the reservation for explicit recovery.
    /// A replay never runs the callback or releases a reservation that another invocation may still own.
    /// </summary>
    public async Task<T> RunAsync<T>(ProbeAdmissionRequest request, Func<CancellationToken, Task<T>> callback, CancellationToken ct = default)
    {
        var admission = await client.AdmitAsync(request, ct);
        if (!admission.MayExecute)
            throw new InvalidOperationException($"Probe reservation {admission.ReservationId} is a replay; execution is not permitted.");
        Exception? callbackFailure = null;
        var cleanupUncertain = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            return await callback(ct);
        }
        catch (ProbeCleanupUncertainException ex)
        {
            cleanupUncertain = true;
            throw new InvalidOperationException(
                $"Probe reservation {admission.ReservationId} is retained pending confirmed process cleanup.", ex);
        }
        catch (Exception ex) { callbackFailure = ex; throw; }
        finally
        {
            if (!cleanupUncertain)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.ReleaseAsync(new(admission.ReservationId), cleanup.Token); }
                catch (Exception ex)
                {
                    var failure = new InvalidOperationException(
                        $"Could not release probe reservation {admission.ReservationId}; retry release after confirming process cleanup.", ex);
                    if (callbackFailure is not null) throw new AggregateException(callbackFailure, failure);
                    throw failure;
                }
            }
        }
    }
}
