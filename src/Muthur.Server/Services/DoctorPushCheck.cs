using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;

namespace Muthur.Server.Services;

/// <summary>Whether work that landed in a merge-mode project is actually reaching its remote.</summary>
public sealed class DoctorPushCheck(Ledger ledger) : IDoctorCheck
{
    public Task<IReadOnlyList<CheckDto>> RunAsync(DoctorContext context, CancellationToken ct = default) =>
        ledger.ReadAsync<IReadOnlyList<CheckDto>>(async (db, _) =>
        {
            var projects = await db.Projects.Where(p => p.LandMode == LandMode.Merge).OrderBy(p => p.Key).ToListAsync(ct);
            var checks = new List<CheckDto>();
            foreach (var project in projects)
            {
                if (project.LastPushError is { } error)
                    checks.Add(new CheckDto("push", project.Key, CheckStatus.Fail,
                        $"Last push of '{project.DefaultBranch}' failed: {error}", project.LastPushAt));
                else if (project.LastPushedCommit is not null)
                    checks.Add(new CheckDto("push", project.Key, CheckStatus.Ok,
                        $"'{project.DefaultBranch}' is on the remote.", project.LastPushAt));
            }
            return checks;
        }, ct);
}
