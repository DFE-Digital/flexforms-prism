using GovUK.Dfe.FlexForms.Prism.Data.Entities;

namespace GovUK.Dfe.FlexForms.Prism.Data.Writing;

/// <summary>
/// Versions that decide whether a projection of the same source revision should replace a stored one.
/// </summary>
public sealed record ProjectionVersions(int ProjectorVersion, int ContractVersion, int ExportPolicyVersion);

public sealed record CurrentProjection(
    Guid TenantId,
    Guid ApplicationId,
    long SourceRevision,
    Guid ResponseId,
    long? ResponseRevision,
    Guid TemplateId,
    Guid TemplateVersionId,
    ApplicationLifecycle Lifecycle,
    byte[] SourceHash,
    ProjectionVersions Versions,
    DateTime? SourceOccurredAt);

public sealed record SubmissionProjection(
    Guid TenantId,
    Guid ApplicationId,
    Guid SubmissionId,
    long SourceRevision,
    Guid ResponseId,
    long? ResponseRevision,
    DateTime SubmittedAt,
    Guid TemplateId,
    Guid TemplateVersionId,
    byte[] SourceHash,
    ProjectionVersions Versions);

public sealed record DeletionRecord(Guid TenantId, Guid ApplicationId, long SourceRevision, DateTime DeletedAt);

public enum WriteOutcome
{
    /// <summary>The write was committed.</summary>
    Applied,

    /// <summary>The stored projection is the same or newer; nothing was written.</summary>
    Stale,

    /// <summary>The application is tombstoned; nothing was written.</summary>
    Tombstoned
}

public sealed record WriteResult(WriteOutcome Outcome, Guid? GenerationId = null, Guid? SupersededGenerationId = null)
{
    public static readonly WriteResult Stale = new(WriteOutcome.Stale);
    public static readonly WriteResult Tombstoned = new(WriteOutcome.Tombstoned);
}
