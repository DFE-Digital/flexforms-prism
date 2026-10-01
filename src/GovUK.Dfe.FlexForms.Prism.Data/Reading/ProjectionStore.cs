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

    public async Task<ExportPolicy> GetExportPolicyAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken)
    {
        var decisions = await db.FieldExportPolicies
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.TemplateId == templateId)
            .Select(p => new { p.ParentFieldId, p.FieldId, p.Decision, p.PolicyVersion })
            .ToListAsync(cancellationToken);

        return new ExportPolicy(
            decisions.Count == 0 ? 0 : decisions.Max(d => d.PolicyVersion),
            decisions.Select(d => new ExportRule(d.ParentFieldId, d.FieldId, d.Decision)));
    }
}
