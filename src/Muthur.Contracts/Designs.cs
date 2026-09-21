namespace Muthur.Contracts;

public sealed record DesignArtifact(string Name, string Role, string State, string Commit, string Path, string Blob);
public sealed record DesignDefinition(string Summary, string Constraints, string Accessibility, string Acceptance,
    IReadOnlyList<DesignArtifact> Artifacts);
public sealed record DesignRevision(int Revision, string Sha256, DesignDefinition Definition, string Actor, DateTimeOffset At);
public sealed record DesignApproval(int Revision, string Sha256, string SpecSha256, bool Approved, string Actor, DateTimeOffset At, string Reason);
public sealed record DesignCheck(int Revision, Guid Subject, string ImplementationSha, bool Matches, string VisualEvidence,
    string InteractionEvidence, string Actor, DateTimeOffset At);
public sealed record DesignDto(string Task, int Revision, IReadOnlyList<DesignRevision> Revisions, IReadOnlyList<DesignApproval> Approvals,
    IReadOnlyList<DesignCheck> Checks, string ApprovalStatus);
public sealed record WriteDesignRequest(int ExpectedRevision, DesignDefinition Definition);
public sealed record ApproveDesignRequest(int ExpectedRevision, bool Approved, string Reason, string SpecCommit);
public sealed record CheckDesignRequest(int ExpectedRevision, Guid Subject, bool Matches, string VisualEvidence, string InteractionEvidence);
