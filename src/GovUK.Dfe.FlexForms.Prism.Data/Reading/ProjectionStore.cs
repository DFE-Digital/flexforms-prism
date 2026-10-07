using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Reading;

public sealed class ProjectionStore(PrismDbContext db) : IProjectionStore
{
    public async Task<StoredProjectionState?> GetStateAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        var state = await db.ApplicationProjectionStates
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.ApplicationId == applicationId)
            .Select(s => new
            {
                s.SourceRevision,
                s.Lifecycle,
                s.ActiveGenerationId,
                s.SourceHash,
                s.TemplateId,
                s.ProjectorVersion,
                s.ContractVersion,
                s.ExportPolicyVersion,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return state is null
            ? null
            : new StoredProjectionState(
                state.SourceRevision,
                state.Lifecycle,
                state.ActiveGenerationId,
                state.SourceHash,
                state.TemplateId,
                new ProjectionVersions(state.ProjectorVersion, state.ContractVersion, state.ExportPolicyVersion));
    }

    public async Task<IReadOnlyDictionary<Guid, StoredSummary>> GetSummariesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> applicationIds,
        CancellationToken cancellationToken)
    {
        if (applicationIds.Count == 0)
        {
            return new Dictionary<Guid, StoredSummary>();
        }

        var ids = applicationIds.Distinct().ToList();
        var states = await db.ApplicationProjectionStates
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && ids.Contains(s.ApplicationId))
            .Select(s => new { s.ApplicationId, s.SourceRevision, s.Lifecycle, s.ProjectorVersion, s.ContractVersion, s.ExportPolicyVersion })
            .ToListAsync(cancellationToken);

        var tombstoned = (await db.DeletionTombstones
                .AsNoTracking()
                .Where(t => t.TenantId == tenantId && ids.Contains(t.ApplicationId))
                .Select(t => t.ApplicationId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return states.ToDictionary(
            s => s.ApplicationId,
            s => new StoredSummary(
                s.SourceRevision,
                s.Lifecycle,
                new ProjectionVersions(s.ProjectorVersion, s.ContractVersion, s.ExportPolicyVersion),
                tombstoned.Contains(s.ApplicationId)));
    }

    public Task<bool> IsTombstonedAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
        => db.DeletionTombstones.AnyAsync(t => t.TenantId == tenantId && t.ApplicationId == applicationId, cancellationToken);

    public async Task<StoredSubmission?> GetSubmissionAsync(Guid tenantId, Guid submissionId, CancellationToken cancellationToken)
    {
        var snapshot = await db.SubmissionSnapshots
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.SubmissionId == submissionId)
            .Select(s => new { s.SelectedGenerationId, s.SourceRevision, s.ProjectorVersion, s.ContractVersion, s.ExportPolicyVersion })
            .SingleOrDefaultAsync(cancellationToken);

        return snapshot is null
            ? null
            : new StoredSubmission(
                snapshot.SelectedGenerationId,
                snapshot.SourceRevision,
                new ProjectionVersions(snapshot.ProjectorVersion, snapshot.ContractVersion, snapshot.ExportPolicyVersion));
    }

    public Task<ExportPolicy> GetExportPolicyAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken)
        => ExportPolicyReader.GetPolicyAsync(db, tenantId, templateId, cancellationToken);
}
