using System.Text.Json;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Server.Services;

public static class Mapper
{
    public static ProjectDto ToDto(this Project p) =>
        new(p.Id, p.Key, p.Name, p.RepoPath, p.DefaultBranch, p.LandMode, p.RequiredValidators, p.CreatedAt);

    public static AgentDto ToDto(this Agent a, LeasePolicy leases, DateTimeOffset now, IReadOnlyList<string> roles, int openTasks) =>
        new(a.Id, a.Name, leases.StatusOf(a, now), a.LastHeartbeat, a.LimitedUntil, a.Summary, a.Harness, a.Model, a.Tier, a.Account, roles, openTasks);

    /// <summary>Requires <see cref="WorkTask.Project"/> and <see cref="WorkTask.Owner"/> to be loaded.</summary>
    public static TaskDto ToDto(this WorkTask t, IReadOnlyList<ValidationDto>? validations = null) =>
        new(Wire.TaskId(t.Id),
            t.Project?.Key ?? "",
            t.Title,
            t.Body,
            t.State,
            t.Priority,
            t.Owner?.Name,
            t.ClaimExpires,
            t.SpecPath,
            t.Branch,
            t.PrUrl,
            t.ParentId is { } parent ? Wire.TaskId(parent) : null,
            t.CreatedAt,
            t.UpdatedAt,
            t.DoneAt,
            validations ?? []);

    public static EventDto ToDto(this LedgerEvent e)
    {
        using var doc = JsonDocument.Parse(e.PayloadJson);
        return new EventDto(e.Seq, e.At, e.Actor, e.ActorModel, e.Type, e.TaskId is { } id ? Wire.TaskId(id) : null, doc.RootElement.Clone());
    }
}
