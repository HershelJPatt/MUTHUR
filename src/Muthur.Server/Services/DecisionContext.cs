using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Core.Entities;
using Muthur.Data;

namespace Muthur.Server.Services;

/// <summary>A bounded reminder of recent decisions. The ledger remains the authoritative complete record.</summary>
internal static class DecisionContext
{
    internal static async Task<string?> ReadAsync(MuthurDb db, int taskId, CancellationToken ct)
    {
        var requests = await db.FounderRequests.Where(r => r.TaskId == taskId && r.Status == RequestStatus.Answered).ToListAsync(ct);
        if (requests.Count == 0) return null;
        var events = await db.Events.Where(e => e.TaskId == taskId && e.Type == "request.answered").ToListAsync(ct);
        var authors = new Dictionary<int, string>();
        foreach (var e in events.OrderBy(e => e.Seq))
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            if (doc.RootElement.TryGetProperty("request", out var id) && id.TryGetInt32(out var key)) authors[key] = e.Actor;
        }
        return Compose(requests, authors);
    }

    internal static string Compose(IEnumerable<FounderRequest> requests, IReadOnlyDictionary<int, string> authors) =>
        string.Join("\n\n", requests.OrderByDescending(r => r.AnsweredAt).ThenByDescending(r => r.Id).Take(5).Select(r =>
            $"Request #{r.Id}; answered {r.AnsweredAt:O}; actor: {authors.GetValueOrDefault(r.Id, "unknown (check ledger)")}\n" +
            $"Question: {Clip(r.Question, 500)}\nAnswer: {Clip(r.Answer ?? "", 1200)}"));

    private static string Clip(string value, int limit) => value.Length <= limit ? value :
        value[..limit] + " [TRUNCATED: read the complete request and answer in task show before acting]";
}
