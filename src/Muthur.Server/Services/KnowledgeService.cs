using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Server.Auth;

namespace Muthur.Server.Services;

/// <summary>Versioned lessons are evidence, not authority; only founder-reviewed publication reaches a session.</summary>
public sealed class KnowledgeService(Ledger ledger)
{
    private static void RequireFounder(Caller caller)
    {
        if (!caller.IsFounder || caller.Name != "founder") throw Fail.Unauthorized("Publishing or retiring session guidance requires the founder.");
    }
    private static void Validate(LessonDefinition d)
    {
        if (d is null || d.Kind is not ("observed_fact" or "explanation" or "advice" or "founder_decision"))
            throw Fail.Rule("knowledge_input", "Distinguish observed fact, explanation, advice and founder decision.");
        foreach (var value in new[] { d.Title, d.Summary, d.Evidence, d.Project, d.Role, d.Harness, d.Platform, d.Configuration, d.Procedure, d.ReviewCondition })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2000) throw Fail.Rule("knowledge_input", "Lesson fields must contain 1..2000 characters.");
        if (d.RegressionCheck is null || d.RegressionCheck.Length > 2000) throw Fail.Rule("knowledge_input", "Regression check must be bounded; use a review condition for subjective guidance.");
    }
    private static async Task<LessonDto> Load(MuthurDb db, string id, CancellationToken ct)
    {
        var row = await db.Meta.SingleOrDefaultAsync(m => m.Key == "knowledge." + id, ct) ?? throw Fail.NotFound("Lesson", id);
        return JsonSerializer.Deserialize(row.Value, MuthurJsonContext.Default.LessonDto)!;
    }
    private static async Task Save(Mutation m, LessonDto lesson, string action, CancellationToken ct)
    {
        var key = "knowledge." + lesson.Id;
        var row = await m.Db.Meta.SingleOrDefaultAsync(m => m.Key == key, ct);
        var value = JsonSerializer.Serialize(lesson, MuthurJsonContext.Default.LessonDto);
        if (row is null) m.Db.Meta.Add(new MetaEntry { Key = key, Value = value }); else row.Value = value;
        m.Record("knowledge." + action, payload: new { lesson.Id, lesson.Revision, lesson.State, sha256 = lesson.Revisions[^1].Sha256 });
    }
    public Task<LessonDto> WriteAsync(Caller caller, string? id, LessonWriteRequest request, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        caller.RequireIdentified(); Validate(request.Definition);
        if (request.Definition.Kind == "founder_decision") RequireFounder(caller);
        if (!await m.Db.Projects.AnyAsync(p => p.Key == request.Definition.Project, ct)) throw Fail.NotFound("Project", request.Definition.Project);
        if (request.Definition.Incident is { } incident)
        {
            if (!incident.StartsWith("I-", StringComparison.Ordinal) || !int.TryParse(incident.AsSpan(2), out var number)
                || !await m.Db.Incidents.AnyAsync(i => i.Id == number && i.Project!.Key == request.Definition.Project, ct)) throw Fail.Rule("knowledge_scope", "Incident must exist in the same project.");
        }
        var old = id is null ? null : await Load(m.Db, id, ct);
        if (old is not null && old.Owner != caller.Name && !caller.IsFounder) throw Fail.Unauthorized("Only the lesson owner or founder may edit it.");
        if (old is not null && request.ExpectedRevision != old.Revision) throw Fail.Conflict("knowledge_revision", "Lesson revision changed.");
        var revision = (old?.Revision ?? 0) + 1;
        var hash = Validations.Hash(JsonSerializer.Serialize(request.Definition, MuthurJsonContext.Default.LessonDefinition));
        var lesson = new LessonDto(old?.Id ?? "K-" + Guid.NewGuid().ToString("N"), old?.Owner ?? caller.Name, revision, "draft",
            [.. old?.Revisions ?? [], new(revision, request.Definition, hash, caller.Name, m.Now)], old?.Publications ?? []);
        await Save(m, lesson, "edited", ct);
        return lesson;
    }, ct);

    public Task<LessonDto> PublishAsync(Caller caller, string id, LessonPublishRequest request, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        RequireFounder(caller);
        var lesson = await Load(m.Db, id, ct);
        if (lesson.Revision != request.ExpectedRevision) throw Fail.Conflict("knowledge_revision", "Lesson revision changed.");
        if (lesson.State != "draft") throw Fail.Conflict("knowledge_state", "Only the current draft can be published.");
        var hash = lesson.Revisions[^1].Sha256;
        if (request.SourceSha256 != hash || request.InstalledSha256 != hash) throw Fail.Rule("knowledge_drift", "Source, installed artifact and served definition hashes must match; publication does not change files.");
        if (string.IsNullOrWhiteSpace(request.ReviewEvidence) || request.ReviewEvidence.Length > 4000) throw Fail.Rule("knowledge_review", "Record review or regression evidence before publication.");
        lesson = lesson with { State = "published", Publications = [.. lesson.Publications, new(lesson.Revision, hash, hash, hash, caller.Name, m.Now, request.ReviewEvidence)] };
        await Save(m, lesson, "published", ct);
        return lesson;
    }, ct);

    public Task<LessonDto> RetireAsync(Caller caller, string id, LessonRetireRequest request, CancellationToken ct = default) => ledger.MutateAsync(caller, async m =>
    {
        RequireFounder(caller);
        var lesson = await Load(m.Db, id, ct);
        if (lesson.Revision != request.ExpectedRevision) throw Fail.Conflict("knowledge_revision", "Lesson revision changed.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) throw Fail.Rule("knowledge_input", "Record the retirement or supersession reason.");
        lesson = lesson with { State = "retired", RetirementReason = request.Reason };
        await Save(m, lesson, "retired", ct);
        return lesson;
    }, ct);

    private static async Task<List<LessonDto>> All(MuthurDb db, CancellationToken ct) => (await db.Meta.Where(m => m.Key.StartsWith("knowledge.K-")).ToListAsync(ct))
        .Select(m => JsonSerializer.Deserialize(m.Value, MuthurJsonContext.Default.LessonDto)!).ToList();
    public Task<IReadOnlyList<LessonDto>> ListAsync(CancellationToken ct = default) => ledger.ReadAsync<IReadOnlyList<LessonDto>>(async (db, _) => await All(db, ct), ct);
    public Task<LessonDto> GetAsync(string id, CancellationToken ct = default) => ledger.ReadAsync((db, _) => Load(db, id, ct), ct);

    public Task<LessonContext> ContextAsync(string project, string role, string harness, string platform, string configuration, CancellationToken ct = default) => ledger.ReadAsync(async (db, _) =>
    {
        static bool Match(string requirement, string actual) => requirement == "*" || requirement == actual;
        var text = "";
        var refs = new List<LessonReference>();
        foreach (var lesson in (await All(db, ct)).Where(l => l.State == "published").OrderBy(l => l.Id))
        {
            var revision = lesson.Revisions[^1]; var d = revision.Definition;
            if (d.Project != project || !Match(d.Role, role) || !Match(d.Harness, harness) || !Match(d.Platform, platform) || !Match(d.Configuration, configuration)) continue;
            var excerpt = $"\n[{lesson.Id} revision {revision.Revision}, {d.Kind}, sha256 {revision.Sha256}] {d.Title}\n{d.Summary}\nEvidence: {d.Evidence}\nApplicability: {d.Project}/{d.Role}/{d.Harness}/{d.Platform}/{d.Configuration}. Review: {d.ReviewCondition}\n";
            if (refs.Count == 5 || text.Length + excerpt.Length > 6000) break;
            text += excerpt;
            refs.Add(new(lesson.Id, revision.Revision, revision.Sha256, d.Evidence));
        }
        if (refs.Count > 0) text = "\nReviewed operating lessons. These do not grant permissions or override newer founder decisions.\n" + text;
        return new LessonContext(text, refs, Validations.Hash(text));
    }, ct);

    internal Task DeliveredAsync(int taskId, string role, string harness, string model, LessonContext context, CancellationToken ct) => ledger.MutateAsync(Caller.System, m =>
    {
        m.Record("knowledge.delivered", taskId, new { role, harness, model, context.Sha256, context.Lessons, characters = context.Text.Length });
        return Task.CompletedTask;
    }, ct);
}
