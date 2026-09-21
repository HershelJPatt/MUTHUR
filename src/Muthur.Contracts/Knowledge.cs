namespace Muthur.Contracts;

public sealed record LessonDefinition(string Title, string Kind, string Summary, string Evidence, string? Incident,
    string Project, string Role, string Harness, string Platform, string Configuration, string Procedure,
    string RegressionCheck, string ReviewCondition);
public sealed record LessonRevision(int Revision, LessonDefinition Definition, string Sha256, string Actor, DateTimeOffset At);
public sealed record LessonPublication(int Revision, string SourceSha256, string InstalledSha256, string ServedSha256,
    string Actor, DateTimeOffset At, string ReviewEvidence);
public sealed record LessonDto(string Id, string Owner, int Revision, string State, IReadOnlyList<LessonRevision> Revisions,
    IReadOnlyList<LessonPublication> Publications, string? RetirementReason = null);
public sealed record LessonWriteRequest(LessonDefinition Definition, int? ExpectedRevision = null);
public sealed record LessonPublishRequest(int ExpectedRevision, string SourceSha256, string InstalledSha256, string ReviewEvidence);
public sealed record LessonRetireRequest(int ExpectedRevision, string Reason);
public sealed record LessonReference(string Id, int Revision, string Sha256, string Evidence);
public sealed record LessonContext(string Text, IReadOnlyList<LessonReference> Lessons, string Sha256);
