using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

public sealed record OperationView(
    Guid OperationId,
    OperationKind Kind,
    Guid? TenantId,
    DateTime? ModifiedSince,
    string RequestedBy,
    BackfillStatus Status,
    int PagesProcessed,
    int ApplicationsScanned,
    int MessagesEnqueued,
    string? CancelledBy,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt)
{
    public static OperationView From(BackfillOperation o) => new(
        o.OperationId, o.Kind, o.TenantId, o.ModifiedSince, o.RequestedBy, o.Status, o.PagesProcessed, o.ApplicationsScanned,
        o.MessagesEnqueued, o.CancelledBy, o.Error, o.CreatedAt, o.StartedAt, o.CompletedAt);
}

public enum CancelOutcome
{
    Cancelled,
    NotFound,
    AlreadyFinished
}

/// <summary>Records control-plane operations. The worker does the actual paging and enqueuing.</summary>
public sealed class OperationService(PrismDbContext db, TimeProvider clock)
{
    public async Task<OperationView> CreateAsync(
        OperationKind kind,
        Guid? tenantId,
        DateTime? modifiedSince,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var operation = new BackfillOperation
        {
            OperationId = Guid.CreateVersion7(),
            Kind = kind,
            TenantId = tenantId,
            ModifiedSince = modifiedSince,
            RequestedBy = requestedBy,
            Status = BackfillStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.BackfillOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        return OperationView.From(operation);
    }

    /// <summary>
    /// Backfills a tenant after its export settings change. Repeated changes would otherwise each re-project every
    /// application: a backfill for the tenant that has not started yet is reused, and one already running is cancelled
    /// because the new one re-projects everything against the latest settings anyway.
    /// </summary>
    public async Task<OperationView> RequestTenantBackfillAsync(Guid tenantId, string requestedBy, CancellationToken cancellationToken)
    {
        var sameScope = db.BackfillOperations.Where(o => o.Kind == OperationKind.Backfill && o.TenantId == tenantId && o.ModifiedSince == null);

        var pending = await sameScope.AsNoTracking().FirstOrDefaultAsync(o => o.Status == BackfillStatus.Pending, cancellationToken);
        if (pending is not null)
        {
            return OperationView.From(pending);
        }

        var operation = await CreateAsync(OperationKind.Backfill, tenantId, null, requestedBy, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        await sameScope
            .Where(o => o.Status == BackfillStatus.Running && o.OperationId != operation.OperationId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.Status, BackfillStatus.Cancelled)
                    .SetProperty(o => o.CancelledBy, $"superseded by {operation.OperationId}")
                    .SetProperty(o => o.CompletedAt, now)
                    .SetProperty(o => o.UpdatedAt, now),
                cancellationToken);

        return operation;
    }

    public async Task<OperationView?> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await db.BackfillOperations.AsNoTracking().SingleOrDefaultAsync(o => o.OperationId == operationId, cancellationToken);
        return operation is null ? null : OperationView.From(operation);
    }

    public Task<bool> HasActiveAsync(OperationKind kind, CancellationToken cancellationToken)
        => db.BackfillOperations.AnyAsync(
            o => o.Kind == kind && (o.Status == BackfillStatus.Pending || o.Status == BackfillStatus.Running),
            cancellationToken);

    public async Task<(CancelOutcome Outcome, OperationView? Operation)> CancelAsync(Guid operationId, string cancelledBy, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await db.BackfillOperations
            .Where(o => o.OperationId == operationId && (o.Status == BackfillStatus.Pending || o.Status == BackfillStatus.Running))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.Status, BackfillStatus.Cancelled)
                    .SetProperty(o => o.CancelledBy, cancelledBy)
                    .SetProperty(o => o.CompletedAt, now)
                    .SetProperty(o => o.UpdatedAt, now),
                cancellationToken);

        var operation = await GetAsync(operationId, cancellationToken);
        return (operation, updated) switch
        {
            (null, _) => (CancelOutcome.NotFound, null),
            (_, 0) => (CancelOutcome.AlreadyFinished, operation),
            _ => (CancelOutcome.Cancelled, operation),
        };
    }
}
