using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>
/// Every state change in the hub goes through <see cref="MutateAsync{T}"/>: one writer at a time, one transaction,
/// row changes and their ledger events committed together, then announced on the <see cref="EventFeed"/>.
/// </summary>
public sealed class Ledger(IDbContextFactory<MuthurDb> factory, TimeProvider clock, EventFeed feed)
{
    private readonly SemaphoreSlim _writer = new(1, 1);

    public async Task<T> MutateAsync<T>(Caller caller, Func<Mutation, Task<T>> work, CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var mutation = new Mutation(db, caller, clock.GetUtcNow());
            var result = await work(mutation);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            feed.Publish(mutation.Recorded);
            return result;
        }
        finally
        {
            _writer.Release();
        }
    }

    public Task MutateAsync(Caller caller, Func<Mutation, Task> work, CancellationToken ct = default) =>
        MutateAsync(caller, async m => { await work(m); return true; }, ct);

    public async Task<T> ReadAsync<T>(Func<MuthurDb, DateTimeOffset, Task<T>> work, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return await work(db, clock.GetUtcNow());
    }
}

/// <summary>The unit of work handed to a mutation: the context, who is acting, the time, and the event recorder.</summary>
public sealed class Mutation(MuthurDb db, Caller caller, DateTimeOffset now)
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public MuthurDb Db { get; } = db;
    public Caller Caller { get; } = caller;
    public DateTimeOffset Now { get; } = now;
    public List<LedgerEvent> Recorded { get; } = [];

    public void Record(string type, int? taskId = null, object? payload = null)
    {
        var e = new LedgerEvent
        {
            At = Now,
            Actor = Caller.Name,
            ActorAgentId = Caller.AgentId,
            ActorModel = Caller.Model,
            Type = type,
            TaskId = taskId,
            PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload, PayloadJson),
        };
        Db.Events.Add(e);
        Recorded.Add(e);
    }
}
