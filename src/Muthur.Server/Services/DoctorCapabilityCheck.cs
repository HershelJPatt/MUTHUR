using Muthur.Contracts;
using Muthur.Launch;

namespace Muthur.Server.Services;

public sealed class DoctorCapabilityCheck(MuthurOptions options) : IDoctorCheck
{
    public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
    {
        var directory = Path.Combine(options.DataDir, "capabilities");
        var count = 0;
        var stale = 0;
        var unknown = 0;
        try
        {
            if (Directory.Exists(directory))
            {
                var store = new CapabilityStore(directory);
                foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(1001))
                {
                    ct.ThrowIfCancellationRequested();
                    if (count >= 1000) { unknown++; break; }
                    var read = store.ReadFile(file);
                    if (read.Diagnostic is not null) unknown++;
                    count += read.Observations.Length;
                    stale += read.Observations.Count(o => o.ExpiresAt <= context.Now || o.ObservedAt > context.Now);
                    unknown += read.Observations.Count(o => o.State == CapabilityStates.Unknown);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unknown++; }
        return Task.FromResult<IReadOnlyList<CheckDto>>([new("capability", "cache", CheckStatus.Warn,
            $"Cached observations: {count}; stale: {stale}; unknown/malformed: {unknown}. Unobserved identities have unknown coverage. " +
            "Use capability inspect with the committed spec/base and exact launch path. capability probe requires shared admission (currently unavailable). Doctor never starts model probes, including --probe.")]);
    }
}
