using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Launch;

namespace Muthur.Server.Services;

/// <summary>Read helpers for per-task validation verdicts.</summary>
public static class Validations
{
    public const string Recovery = "Run muthur task revalidate <id> --reason <text>, explicitly reattach the frozen spec with muthur task spec, then run muthur task implemented again and obtain fresh verdicts.";
    public const string EvidenceHelp = "Supply --evidence <report> or --evidence-file <UTF-8 file>: at least 20 trimmed characters describing the check, observation, and an artifact/reference or reproduction command. For blocked work, say what stopped you.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool UsefulEvidence(string? text) => text?.Trim().Length >= 20;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    private static string RepositoryPath(Project project) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RepoPath));

    public static ValidationSubjectDto ToDto(this ValidationSubject subject) => new(
        subject.Id, Wire.TaskId(subject.TaskId), subject.ProjectId, subject.RepositoryPath, subject.ImplementationSha,
        subject.SpecPath, subject.SpecSha256, Deserialize<List<string>>(subject.RequiredValidatorsJson),
        Deserialize<ValidationChecksDto>(subject.RequiredChecksJson),
        Deserialize<ValidationEnvironmentDto>(subject.InvalidatingEnvironmentJson),
        Deserialize<ValidationMetadataDto>(subject.DescriptiveMetadataJson), subject.CreatedAt);

    private static string Roles(Project project) => Serialize(project.RequiredValidators.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

    private static async Task<string> EnvironmentAsync(MuthurDb db, Project project, CancellationToken ct)
    {
        var roles = await db.Roles.Where(r => project.RequiredValidators.Contains(r.Key)).ToListAsync(ct);
        var briefs = project.RequiredValidators.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key => new ValidatorBriefDigestDto(key, roles.SingleOrDefault(r => r.Key == key) is { } role ? Hash(role.BriefMd) : "missing"))
            .ToList();
        return Serialize(new ValidationEnvironmentDto(project.Key, project.DefaultBranch, project.LandMode.ToWire(), briefs));
    }

    /// <summary>
    /// The checks the round is judged by, all read at the implementation commit: build and test from the committed
    /// muthur.project.json, and what the committed spec wrote under its Verification heading.
    /// </summary>
    private static async Task<string> ChecksAsync(ITaskLander lander, Project project, string sha, string spec, CancellationToken ct)
    {
        var verification = ParseVerification(spec);
        var config = await lander.ReadFileAsync(project, sha, "muthur.project.json", ct);
        if (config is null) return Serialize(new ValidationChecksDto(null, null, verification.Commands, verification.RequiresJudgment));
        try
        {
            using var document = JsonDocument.Parse(config);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            string? Command(string name)
            {
                if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind != JsonValueKind.String) throw new JsonException();
                return value.GetString();
            }
            return Serialize(new ValidationChecksDto(Command("build"), Command("test"), verification.Commands, verification.RequiresJudgment));
        }
        catch (JsonException)
        {
            throw Fail.Rule("validation_config_invalid", "Committed muthur.project.json must be a JSON object with string build/test commands. Fix it, commit, and resubmit the implementation.");
        }
    }

    /// <summary>The spec at the subject's commit, or nothing: a subject whose spec has gone is judged as one that wrote no checks.</summary>
    private static async Task<string> ChecksAsync(ITaskLander lander, ValidationSubject subject, Project project, CancellationToken ct) =>
        await ChecksAsync(lander, project, subject.ImplementationSha,
            await lander.ReadFileAsync(project, subject.ImplementationSha, subject.SpecPath, ct) ?? "", ct);

    internal static SpecVerification ParseVerification(string spec)
    {
        try { return SpecVerification.Parse(spec); }
        catch (WorkerDispatchException ex) { throw Fail.Rule(ex.Code, ex.Message); }
    }

    public static async Task<ValidationSubject> CreateAsync(Mutation m, WorkTask task, string sha, ITaskLander lander, CancellationToken ct)
    {
        var project = task.Project!;
        var spec = await lander.ReadFileAsync(project, sha, task.SpecPath!, ct)
            ?? throw Fail.Rule("spec_missing", "The submitted implementation must contain the attached frozen spec.");
        TaskService.RequireSpecHeading(task, task.SpecPath!, spec);
        var digest = Hash(spec);
        if (task.SpecSha256 is null || digest != task.SpecSha256)
            throw Fail.Rule("spec_changed", "The submitted spec differs from the frozen attachment, or its historical digest is unknown. Explicitly reattach the intended committed spec with muthur task spec, then submit implemented again.");
        return new ValidationSubject
        {
            Id = Guid.NewGuid(), TaskId = task.Id, ProjectId = task.ProjectId,
            RepositoryPath = RepositoryPath(project), ImplementationSha = sha,
            SpecPath = task.SpecPath!, SpecSha256 = digest, RequiredValidatorsJson = Roles(project),
            RequiredChecksJson = await ChecksAsync(lander, project, sha, spec, ct),
            InvalidatingEnvironmentJson = await EnvironmentAsync(m.Db, project, ct),
            DescriptiveMetadataJson = Serialize(new ValidationMetadataDto(RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
                typeof(Validations).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                await DesignService.ApprovedHashAsync(m.Db, task, spec, ct))), CreatedAt = m.Now,
        };
    }

    public static async Task<bool> CompatibleAsync(MuthurDb db, WorkTask task, CancellationToken ct)
    {
        var s = task.CurrentSubject;
        var p = task.Project!;
        return s is not null && task.ValidationInvalidationReason is null && s.ProjectId == task.ProjectId && !string.IsNullOrWhiteSpace(p.RepoPath)
            && s.RepositoryPath == RepositoryPath(p) && s.SpecPath == task.SpecPath && s.SpecSha256 == task.SpecSha256
            && s.RequiredValidatorsJson == Roles(p) && s.InvalidatingEnvironmentJson == await EnvironmentAsync(db, p, ct)
            && await DesignCompatibleAsync(db, task, ct);
    }

    private static async Task<bool> DesignCompatibleAsync(MuthurDb db, WorkTask task, CancellationToken ct)
    {
        try { return task.CurrentSubject!.ToDto().DescriptiveMetadata.DesignSha256 == await DesignService.ApprovedHashAsync(db, task, null, ct); }
        catch (MuthurException) { return false; }
    }

    public static async Task RequireCurrentAsync(MuthurDb db, WorkTask task, ITaskLander lander, CancellationToken ct)
    {
        if (task.CurrentSubject is null) throw Fail.Rule("validation_provenance_unknown", Recovery);
        if (!await CompatibleAsync(db, task, ct)
            || task.CurrentSubject.RequiredChecksJson != await ChecksAsync(lander, task.CurrentSubject, task.Project!, ct))
            throw Fail.Conflict("validation_subject_changed", "Validation policy changed. " + Recovery);
    }

    public static async Task<TaskDto> MapAsync(MuthurDb db, WorkTask task, IReadOnlyList<ValidationDto>? rows, CancellationToken ct)
    {
        var status = task.CurrentSubject is null
            ? task.ValidationInvalidationReason is null ? "unknown" : "stale"
            : await CompatibleAsync(db, task, ct) ? "current" : "stale";
        return task.ToDto(rows) with
        {
            ProvenanceStatus = status,
            Validations = (rows ?? []).Select(row => row with
            {
                EvidenceStatus = row.SubjectId is null ? "unknown"
                    : status != "current" || row.SubjectId != task.CurrentSubjectId ? "stale"
                    : UsefulEvidence(row.Evidence) ? "reported" : "missing",
            }).ToList(),
        };
    }

    public static async Task<Dictionary<int, IReadOnlyList<ValidationDto>>> ForTasksAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];
        var rows = await db.TaskValidations.Include(v => v.Agent).Include(v => v.ClaimedBy)
            .Where(v => taskIds.Contains(v.TaskId))
            .OrderBy(v => v.ValidatorKey)
            .ToListAsync(ct);
        return rows.GroupBy(v => v.TaskId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ValidationDto>)g.Select(v => new ValidationDto(
                v.ValidatorKey, v.Verdict.ToWire(), v.Agent?.Name, v.Evidence, v.At,
                v.WaitingSince, v.ClaimedBy?.Name, v.ClaimExpires, v.SubjectId,
                v.SubjectId is null ? "unknown" : UsefulEvidence(v.Evidence) ? "reported" : "missing")).ToList());
    }
}
