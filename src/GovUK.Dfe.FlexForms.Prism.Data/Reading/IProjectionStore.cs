using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;

namespace GovUK.Dfe.FlexForms.Prism.Data.Reading;

/// <summary>What has already been projected for an application.</summary>
public sealed record StoredProjectionState(
    long SourceRevision,
    ApplicationLifecycle Lifecycle,
    Guid? ActiveGenerationId,
    byte[]? SourceHash,
    Guid? TemplateId,
    ProjectionVersions Versions);

/// <summary>What has already been projected for a submission.</summary>
public sealed record StoredSubmission(Guid SelectedGenerationId, long SourceRevision, ProjectionVersions Versions);

/// <summary>
/// Unlocked reads used to skip work early. They are only hints: the writer re-checks every decision under
/// lock before it commits.
/// </summary>
public interface IProjectionStore
{
    Task<StoredProjectionState?> GetStateAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken);

    Task<bool> IsTombstonedAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken);

    Task<StoredSubmission?> GetSubmissionAsync(Guid tenantId, Guid submissionId, CancellationToken cancellationToken);

    /// <summary>
    /// The template's export decisions. Its version is the highest decision version, or 0 when nothing has been
    /// decided yet, so it only grows as long as decisions are updated rather than deleted.
    /// </summary>
    Task<ExportPolicy> GetExportPolicyAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken);
}
