using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

/// <summary>
/// Works through pending and running operations, oldest first. Each page of source applications is turned
/// into Resync messages and the progress saved, so a run can stop at its time budget and the next run resumes
/// from the saved tenant and page. Re-sending a page after a crash is harmless: the message ids are
/// deterministic and the projector ignores revisions it already has.
/// </summary>
public sealed partial class OperationProcessor(
    PrismDbContext db,
    ISourceClient source,
    IProjectionStore store,
    IProjectionRequestSender sender,
    ControlPlaneMetrics metrics,
    TimeProvider clock,
    IOptions<PrismFunctionsOptions> options,
    ILogger<OperationProcessor> logger)
{
    private const int MaxErrorLength = 2000;

    private static readonly ProjectionVersions CurrentVersions = new(PrismVersions.ProjectorVersion, PrismVersions.ContractVersion, 0);

    private BackfillOptions Settings => options.Value.Backfill;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + Settings.TimeBudget;
        while (clock.GetUtcNow() < deadline)
        {
            var operation = await db.BackfillOperations
                .AsNoTracking()
                .Where(o => o.Status == BackfillStatus.Pending || o.Status == BackfillStatus.Running)
                .OrderBy(o => o.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (operation is null)
            {
                return;
            }

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["operation_id"] = operation.OperationId,
                ["operation_kind"] = operation.Kind.ToString(),
            });

            try
            {
                if (!await ProcessAsync(operation, deadline, cancellationToken))
                {
                    return;
                }
            }
            catch (SourceException ex) when (!ex.IsTransient)
            {
                LogFailed(ex);
                await FinishAsync(operation, BackfillStatus.Failed, ex.Message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInterrupted(ex);
                await RecordErrorAsync(operation.OperationId, ex.Message, cancellationToken);
                return;
            }
        }
    }

    /// <returns>False when the time budget ran out before the operation finished.</returns>
    private async Task<bool> ProcessAsync(BackfillOperation operation, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (operation.Status == BackfillStatus.Pending && !await StartAsync(operation.OperationId, cancellationToken))
        {
            return true;
        }

        var tenants = operation.TenantId is { } tenantId
            ? [tenantId]
            : (await source.GetTenantsAsync(cancellationToken)).Select(t => t.TenantId).Distinct().Order().ToList();

        var (cursor, page) = (operation.CurrentTenantId, operation.NextPage);
        foreach (var tenant in tenants.Where(t => cursor is null || t.CompareTo(cursor.Value) >= 0))
        {
            if (tenant != cursor)
            {
                (cursor, page) = (tenant, 1);
            }

            while (true)
            {
                if (clock.GetUtcNow() >= deadline)
                {
                    return false;
                }

                var result = await source.ListApplicationsAsync(tenant, operation.ModifiedSince, page, Settings.PageSize, cancellationToken);
                var selected = operation.Kind == OperationKind.Backfill
                    ? result.Items
                    : await DriftedAsync(tenant, result.Items, cancellationToken);

                var now = clock.GetUtcNow().UtcDateTime;
                await sender.SendAsync([.. selected.Select(a => Resync(operation.OperationId, tenant, a, now))], cancellationToken);
                metrics.Enqueued(operation.Kind, selected.Count);

                page++;
                if (!await SaveProgressAsync(operation.OperationId, tenant, page, result.Items.Count, selected.Count, cancellationToken))
                {
                    LogCancelled();
                    return true;
                }

                if (!result.HasMore)
                {
                    break;
                }
            }
        }

        await FinishAsync(operation, BackfillStatus.Completed, null, cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<PrismApplicationSummaryDto>> DriftedAsync(
        Guid tenantId,
        IReadOnlyList<PrismApplicationSummaryDto> applications,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetSummariesAsync(tenantId, [.. applications.Select(a => a.ApplicationId)], cancellationToken);
        var drifted = new List<PrismApplicationSummaryDto>();
        foreach (var application in applications)
        {
            if (DriftOf(application, stored.GetValueOrDefault(application.ApplicationId)) is { } drift)
            {
                metrics.Drift(drift);
                drifted.Add(application);
            }
        }

        return drifted;
    }

    /// <summary>Why Prism's stored state no longer matches the source, or null when it does.</summary>
    internal static string? DriftOf(PrismApplicationSummaryDto source, StoredSummary? stored) => stored switch
    {
        null => "missing",
        { Tombstoned: true } => null,
        _ when source.IsDeleted => "deletion_missing",
        _ when stored.SourceRevision < source.SourceRevision => "behind",
        _ when (stored.Versions with { ExportPolicyVersion = 0 }).CompareTo(CurrentVersions) < 0 => "outdated",
        _ => null,
    };

    private static ApplicationProjectionRequestedEvent Resync(Guid operationId, Guid tenantId, PrismApplicationSummaryDto application, DateTime now) => new(
        ApplicationProjectionRequestedEvent.CurrentContractVersion,
        tenantId,
        application.ApplicationId,
        ProjectionReason.Resync,
        application.SourceRevision,
        ResponseId: null,
        SubmissionId: null,
        TemplateId: null,
        TemplateVersionId: null,
        operationId,
        now);

    private async Task<bool> StartAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.BackfillOperations
            .Where(o => o.OperationId == operationId && o.Status == BackfillStatus.Pending)
            .ExecuteUpdateAsync(
                set => set.SetProperty(o => o.Status, BackfillStatus.Running).SetProperty(o => o.StartedAt, now).SetProperty(o => o.UpdatedAt, now),
                cancellationToken) == 1;
    }

    /// <returns>False when the operation is no longer running, because it was cancelled.</returns>
    private async Task<bool> SaveProgressAsync(Guid operationId, Guid tenantId, int nextPage, int scanned, int enqueued, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.BackfillOperations
            .Where(o => o.OperationId == operationId && o.Status == BackfillStatus.Running)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.CurrentTenantId, tenantId)
                    .SetProperty(o => o.NextPage, nextPage)
                    .SetProperty(o => o.PagesProcessed, o => o.PagesProcessed + 1)
                    .SetProperty(o => o.ApplicationsScanned, o => o.ApplicationsScanned + scanned)
                    .SetProperty(o => o.MessagesEnqueued, o => o.MessagesEnqueued + enqueued)
                    .SetProperty(o => o.UpdatedAt, now),
                cancellationToken) == 1;
    }

    private async Task FinishAsync(BackfillOperation operation, BackfillStatus status, string? error, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var finished = await db.BackfillOperations
            .Where(o => o.OperationId == operation.OperationId && (o.Status == BackfillStatus.Running || o.Status == BackfillStatus.Pending))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(o => o.Status, status)
                    .SetProperty(o => o.Error, Truncate(error))
                    .SetProperty(o => o.CompletedAt, now)
                    .SetProperty(o => o.UpdatedAt, now),
                cancellationToken);

        if (finished == 1)
        {
            metrics.Finished(operation.Kind, status);
            LogFinished(status);
        }
    }

    private Task RecordErrorAsync(Guid operationId, string error, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.BackfillOperations
            .Where(o => o.OperationId == operationId)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.Error, Truncate(error)).SetProperty(o => o.UpdatedAt, now), cancellationToken);
    }

    private static string? Truncate(string? value) => value is null || value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation finished: {Status}")]
    private partial void LogFinished(BackfillStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation was cancelled; stopping")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Operation failed permanently")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation interrupted; it will resume on the next run")]
    private partial void LogInterrupted(Exception exception);
}
