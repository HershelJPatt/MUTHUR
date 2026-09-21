using System.Text;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core.Entities;
using Muthur.Data;

namespace Muthur.Server.Services;

internal static class TaskUnitContext
{
    internal static async Task<TaskResumePacket> ReadAsync(MuthurDb db, WorkTask task, CancellationToken ct)
    {
        var graph = await TaskUnitService.ReadGraph(db, task.Id, ct);
        var requests = await db.FounderRequests.Where(x => x.TaskId == task.Id && x.Status == RequestStatus.Open).ToListAsync(ct);
        var kindKeys = requests.Select(x => $"request.kind.{x.Id}").ToList();
        var kinds = await db.Meta.Where(x => kindKeys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var blockers = requests.Select(x => $"request #{x.Id} ({RequestService.KindFor(kinds, x.Id)})").Concat(task.DependsOn.Select(x => $"task dependency {x}")).ToList();
        var omittedBlockers = Math.Max(0, blockers.Count - 20);
        blockers = blockers.Take(20).ToList();
        if (omittedBlockers > 0) blockers.Add($"Omitted blockers: {omittedBlockers}");
        var rows = graph?.Units.Take(20).Select(x => new TaskUnitResumeRow(x.Id, x.Attempt?.AttemptId,
            x.Attempt?.ReportedState, x.Attempt?.ReviewState, x.Attempt?.IntegrationCommit is null ? "not recorded" : "recorded (advisory)",
            Clip(x.Attempt?.OutputBranch, 250), x.Attempt?.OutputCommit,
            x.Attempt?.ReviewState == "accepted" ? "run reconcile before reuse" : Clip(x.Attempt?.NextAction, 500) ?? "start",
            Clip(x.InvalidationReason, 500))).ToList() ?? [];
        var key = Wire.TaskId(task.Id);
        return new TaskResumePacket(key, graph?.Revision, await DecisionContext.ReadAsync(db, task.Id, ct), blockers, rows,
            Math.Max(0, (graph?.Units.Count ?? 0) - rows.Count), Routes.TaskUnits(key),
            graph is null ? $"muthur task show {key}" : $"Read muthur task units {key}; run per-unit reconcile before reuse; consult muthur task show {key} for full decisions.");
    }

    internal static string? Format(TaskResumePacket packet)
    {
        if (packet.GraphRevision is null) return null;
        var header = $"Stored work-unit context, revision {packet.GraphRevision}; advisory, artifacts have not just been verified.\n";
        var footer = $"\nFull graph: muthur task units {packet.TaskId}\nFull decisions: muthur task show {packet.TaskId}\n" +
            $"Run muthur task resume {packet.TaskId} and per-unit reconcile before reuse. Reuse valid independent outputs.\n";
        var text = new StringBuilder(header);
        var omitted = packet.OmittedUnits;
        foreach (var row in packet.Units)
        {
            var line = $"{row.Id}; attempt={row.AttemptId}; reported={row.ReportedState}; review={row.ReviewState}; integration={row.IntegrationState}; " +
                $"branch={row.OutputBranch}; output={row.OutputCommit}; next={row.NextAction}; invalidation={row.InvalidationReason}\n";
            if (text.Length + line.Length + footer.Length + 100 > 7000) omitted++;
            else text.Append(line);
        }
        text.Append($"Omitted units: {omitted}\n");
        text.Append("Blockers: ").Append(Clip(string.Join("; ", packet.Blockers), 400)).Append('\n');
        text.Append("Latest decisions: ").Append(Clip(packet.LatestDecisions, 400)).Append('\n');
        return text.Append(footer).ToString();
    }

    private static string? Clip(string? value, int limit) => value?.Length > limit ? value[..(limit - 3)] + "..." : value;
}
