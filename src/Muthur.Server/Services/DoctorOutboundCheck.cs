using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

/// <summary>
/// Whether every allowed target still has a channel, an address that channel accepts and, when probing,
/// something answering at the other end. The subject is the target's key: its address is often the credential.
/// </summary>
public sealed class DoctorOutboundCheck(Ledger ledger, IEnumerable<IOutboundChannel> channels) : IDoctorCheck
{
    public async Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default)
    {
        var targets = await ledger.ReadAsync(async (db, _) => await db.OutboundTargets.OrderBy(t => t.Key).ToListAsync(ct), ct);

        var checks = new List<CheckDto>();
        foreach (var target in targets) checks.Add(await TargetAsync(target, context, ct));
        return checks;
    }

    private async Task<CheckDto> TargetAsync(OutboundTarget target, DoctorContext context, CancellationToken ct)
    {
        CheckDto Result(CheckStatus status, string detail) => new("outbound", target.Key, status, detail);

        var channel = channels.FirstOrDefault(c => string.Equals(c.Name, target.Channel, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
            return Result(CheckStatus.Fail, $"No channel named '{target.Channel}'. Known: {string.Join(", ", channels.Select(c => c.Name))}.");

        try
        {
            channel.Validate(target.Address, "");
        }
        catch (MuthurException ex)
        {
            return Result(CheckStatus.Fail, ex.Message);
        }

        if (context.Probe)
        {
            try
            {
                await channel.ProbeAsync(target.Address, ct);
            }
            catch (ChannelException ex)
            {
                return Result(CheckStatus.Fail, $"{channel.Name} is not reachable: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Only a ChannelException promises to say nothing of the address; anything else is summarised, not quoted.
                return Result(CheckStatus.Fail, $"{channel.Name} is not reachable: the probe failed unexpectedly.");
            }
        }

        return Result(CheckStatus.Ok, $"{channel.Name} target, address accepted."
            + (target.RequiresFounderApproval ? " Every message needs the founder." : ""));
    }
}
