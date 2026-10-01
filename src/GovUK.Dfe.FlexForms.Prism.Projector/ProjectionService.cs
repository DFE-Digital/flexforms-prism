using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Enums;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Flattening;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Flattener.Responses;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.FlexForms.Prism.Projector;

public interface IProjectionService
{
    /// <summary>
    /// Projects the application the message refers to. Throws <see cref="PermanentProjectionException"/> when
    /// the message can never succeed; any other exception is transient and the message should be retried.
    /// </summary>
    Task<ProjectionOutcome> ProjectAsync(ApplicationProjectionRequestedEvent message, CancellationToken cancellationToken);

    /// <summary>
    /// Catalogues a newly published template version so its fields can be classified, and its changes reviewed,
    /// before any application uses it. Failures are reported the same way as <see cref="ProjectAsync"/>.
    /// </summary>
    Task CatalogueTemplateVersionAsync(TemplateVersionPublishedEvent message, CancellationToken cancellationToken);
}

/// <summary>
/// Treats every message as a notification: the source is re-read and its revision compared with what has
/// already been projected, so duplicates, reordering and replays converge on the same state.
/// </summary>
public sealed partial class ProjectionService(
    ISourceClient source,
    IProjectionStore store,
    IProjectionWriter writer,
    IFieldCatalogWriter catalogWriter,
    CatalogueCache catalogues,
    PrismMetrics metrics,
    TimeProvider clock,
    ILogger<ProjectionService> logger) : IProjectionService
{
    public async Task<ProjectionOutcome> ProjectAsync(ApplicationProjectionRequestedEvent message, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["tenant_id"] = message.TenantId,
            ["application_id"] = message.ApplicationId,
            ["projection_reason"] = message.Reason.ToString(),
            ["source_revision"] = message.SourceRevision,
            ["response_id"] = message.ResponseId,
            ["submission_id"] = message.SubmissionId,
            ["operation_id"] = message.OperationId,
            ["projector_version"] = PrismVersions.ProjectorVersion,
            ["contract_version"] = message.ContractVersion,
        });

        try
        {
            Validate(message);

            ProjectionOutcome outcome;
            try
            {
                outcome = message.Reason switch
                {
                    ProjectionReason.Saved or ProjectionReason.Resync => await ProjectCurrentAsync(message, null, cancellationToken),
                    ProjectionReason.Submitted => await ProjectSubmittedAsync(message, cancellationToken),
                    ProjectionReason.Deleted => await RecordDeletionAsync(message.TenantId, message.ApplicationId, message.SourceRevision, message.OccurredAt, cancellationToken),
                    _ => throw new PermanentProjectionException(PermanentFailureReasons.InvalidMessage, $"Unknown projection reason {message.Reason}."),
                };
            }
            catch (Exception ex) when (Classify(ex) is { } permanent)
            {
                throw permanent;
            }

            if (outcome.Status == ProjectionStatus.Skipped)
            {
                metrics.Skipped(outcome.Reason);
            }

            metrics.Succeeded(message.Reason, outcome, message.OccurredAt, clock.GetUtcNow().UtcDateTime);
            LogProjected(outcome.Status, outcome.Reason, outcome.GenerationId);
            return outcome;
        }
        catch (PermanentProjectionException ex)
        {
            metrics.Failed(message.Reason, ex.Reason, permanent: true);
            LogPermanentFailure(ex, ex.Reason);
            throw;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            metrics.Failed(message.Reason, ex.GetType().Name, permanent: false);
            LogTransientFailure(ex);
            throw;
        }
    }

    public async Task CatalogueTemplateVersionAsync(TemplateVersionPublishedEvent message, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["tenant_id"] = message.TenantId,
            ["template_id"] = message.TemplateId,
            ["template_version_id"] = message.TemplateVersionId,
            ["template_version_number"] = message.VersionNumber,
            ["projector_version"] = PrismVersions.ProjectorVersion,
            ["contract_version"] = message.ContractVersion,
        });

        try
        {
            Validate(message);

            try
            {
                var (tenantId, templateVersionId) = (message.TenantId, message.TemplateVersionId);
                var template = await TimeSourceAsync("get_template_version", () => source.GetTemplateVersionAsync(tenantId, templateVersionId, cancellationToken));
                if (template.TemplateId != message.TemplateId)
                {
                    throw new PermanentProjectionException(
                        PermanentFailureReasons.SourceMismatch,
                        $"Template version {templateVersionId} belongs to template {template.TemplateId}, not {message.TemplateId}.");
                }

                var policy = await store.GetExportPolicyAsync(tenantId, template.TemplateId, cancellationToken);
                var catalogue = catalogues.GetOrBuild(tenantId, templateVersionId, template.JsonSchema);
                await EnsureCatalogueAsync(tenantId, template, catalogue, policy, force: true, cancellationToken);
                metrics.TemplateVersionCatalogued(catalogue.Fields.Count);
                LogTemplateCatalogued(catalogue.Fields.Count);
            }
            catch (Exception ex) when (Classify(ex) is { } permanent)
            {
                throw permanent;
            }
        }
        catch (PermanentProjectionException ex)
        {
            metrics.TemplateCatalogueFailed(ex.Reason, permanent: true);
            LogPermanentFailure(ex, ex.Reason);
            throw;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            metrics.TemplateCatalogueFailed(ex.GetType().Name, permanent: false);
            LogTransientFailure(ex);
            throw;
        }
    }

    private static void Validate(TemplateVersionPublishedEvent message)
    {
        if (message.ContractVersion != TemplateVersionPublishedEvent.CurrentContractVersion)
        {
            throw new PermanentProjectionException(
                PermanentFailureReasons.UnsupportedContract,
                $"Contract version {message.ContractVersion} is not supported; expected {TemplateVersionPublishedEvent.CurrentContractVersion}.");
        }

        var problem = message switch
        {
            { TenantId: var t } when t == Guid.Empty => "TenantId is required.",
            { TemplateId: var t } when t == Guid.Empty => "TemplateId is required.",
            { TemplateVersionId: var v } when v == Guid.Empty => "TemplateVersionId is required.",
            _ => null,
        };

        if (problem is not null)
        {
            throw new PermanentProjectionException(PermanentFailureReasons.InvalidMessage, problem);
        }
    }

    private static void Validate(ApplicationProjectionRequestedEvent message)
    {
        if (message.ContractVersion != ApplicationProjectionRequestedEvent.CurrentContractVersion)
        {
            throw new PermanentProjectionException(
                PermanentFailureReasons.UnsupportedContract,
                $"Contract version {message.ContractVersion} is not supported; expected {ApplicationProjectionRequestedEvent.CurrentContractVersion}.");
        }

        var problem = message switch
        {
            { TenantId: var t } when t == Guid.Empty => "TenantId is required.",
            { ApplicationId: var a } when a == Guid.Empty => "ApplicationId is required.",
            { SourceRevision: < 0 } => "SourceRevision cannot be negative.",
            { Reason: ProjectionReason.Saved or ProjectionReason.Submitted, ResponseId: null } => $"ResponseId is required for {message.Reason}.",
            { Reason: ProjectionReason.Submitted, SubmissionId: null } => "SubmissionId is required for Submitted.",
            _ => null,
        };

        if (problem is not null)
        {
            throw new PermanentProjectionException(PermanentFailureReasons.InvalidMessage, problem);
        }
    }

    private static PermanentProjectionException? Classify(Exception exception) => exception switch
    {
        PermanentProjectionException => null,
        SourceNotFoundException ex => new(PermanentFailureReasons.SourceNotFound, ex.Message, ex),
        SourceException { IsTransient: false } ex => new(PermanentFailureReasons.SourceRejected, ex.Message, ex),
        TemplateFormatException ex => new(PermanentFailureReasons.UnreadableTemplate, ex.Message, ex),
        FlatteningException ex => new(PermanentFailureReasons.UnreadableResponse, ex.Message, ex),
        _ => null,
    };

    /// <summary>
    /// Saved and Resync: project the application's current response. <paramref name="knownState"/> is source
    /// state the caller has already fetched.
    /// </summary>
    private async Task<ProjectionOutcome> ProjectCurrentAsync(
        ApplicationProjectionRequestedEvent message,
        PrismApplicationStateDto? knownState,
        CancellationToken cancellationToken)
    {
        var (tenantId, applicationId) = (message.TenantId, message.ApplicationId);
        if (await store.IsTombstonedAsync(tenantId, applicationId, cancellationToken))
        {
            return ProjectionOutcome.Skipped(ProjectionReasons.Tombstoned);
        }

        var stored = await store.GetStateAsync(tenantId, applicationId, cancellationToken);
        if (knownState is null && stored is { TemplateId: { } storedTemplateId } && stored.SourceRevision >= message.SourceRevision)
        {
            var storedPolicy = await store.GetExportPolicyAsync(tenantId, storedTemplateId, cancellationToken);
            if (stored.Versions.CompareTo(VersionsFor(storedPolicy)) >= 0)
            {
                return ProjectionOutcome.Skipped(ProjectionReasons.AlreadyProjected);
            }
        }

        var state = knownState ?? await GetApplicationAsync(tenantId, applicationId, cancellationToken);
        if (state.IsDeleted)
        {
            return await RecordDeletionAsync(tenantId, applicationId, state.SourceRevision, state.DeletedOn ?? message.OccurredAt, cancellationToken);
        }

        var policy = await store.GetExportPolicyAsync(tenantId, state.TemplateId, cancellationToken);
        var versions = VersionsFor(policy);

        ProjectionOutcome outcome;
        if (stored is not null && !ProjectionVersions.IsNewer(stored.SourceRevision, stored.Versions, state.SourceRevision, versions))
        {
            outcome = ProjectionOutcome.Skipped(ProjectionReasons.NotNewer);
        }
        else if (state is { ResponseId: { } responseId, ResponseBody: { } body })
        {
            outcome = await WriteCurrentAsync(tenantId, state, responseId, body, stored, policy, versions, cancellationToken);
        }
        else
        {
            outcome = ProjectionOutcome.Skipped(ProjectionReasons.NoResponse);
        }

        if (message.Reason != ProjectionReason.Resync
            || state is not { Status: ApplicationStatus.Submitted, SubmissionId: { } submissionId, SubmittedResponseId: { } submittedResponseId, SubmittedRevision: { } submittedRevision })
        {
            return outcome;
        }

        var submission = await ProjectSubmissionAsync(
            tenantId,
            applicationId,
            new SubmissionTarget(submissionId, submittedResponseId, submittedRevision, state.LastModifiedOn ?? state.CreatedOn, state.TemplateId, state.TemplateVersionId),
            cancellationToken);

        return outcome.Status == ProjectionStatus.Skipped && submission.Status == ProjectionStatus.Projected ? submission : outcome;
    }

    private async Task<ProjectionOutcome> WriteCurrentAsync(
        Guid tenantId,
        PrismApplicationStateDto state,
        Guid responseId,
        string body,
        StoredProjectionState? stored,
        ExportPolicy policy,
        ProjectionVersions versions,
        CancellationToken cancellationToken)
    {
        var flattened = await FlattenAsync(tenantId, state.TemplateVersionId, body, policy, cancellationToken);
        var projection = new CurrentProjection(
            tenantId,
            state.ApplicationId,
            state.SourceRevision,
            responseId,
            state.ResponseRevision,
            state.TemplateId,
            state.TemplateVersionId,
            state.Status == ApplicationStatus.Submitted ? ApplicationLifecycle.Submitted : ApplicationLifecycle.Draft,
            flattened.Hash,
            versions,
            state.LastModifiedOn ?? state.CreatedOn);

        if (stored is { ActiveGenerationId: not null, SourceHash: { } storedHash } && storedHash.AsSpan().SequenceEqual(flattened.Hash))
        {
            var advanced = await TimeWriteAsync("advance_current", () => writer.AdvanceCurrentAsync(projection, cancellationToken));
            if (advanced.Outcome == WriteOutcome.Applied)
            {
                metrics.HashReused();
                return ProjectionOutcome.Projected(ProjectionReasons.HashReused, advanced.GenerationId);
            }

            return Rejected(advanced);
        }

        var written = await TimeWriteAsync("write_current", () => writer.WriteCurrentAsync(projection, flattened.Facts, cancellationToken));
        return Created(nameof(GenerationKind.Current), written);
    }

    /// <summary>
    /// Submitted: freeze the exact submitted response as the submission snapshot, then bring Current up to date
    /// if this is still the latest revision.
    /// </summary>
    private async Task<ProjectionOutcome> ProjectSubmittedAsync(ApplicationProjectionRequestedEvent message, CancellationToken cancellationToken)
    {
        var (tenantId, applicationId) = (message.TenantId, message.ApplicationId);
        if (await store.IsTombstonedAsync(tenantId, applicationId, cancellationToken))
        {
            return ProjectionOutcome.Skipped(ProjectionReasons.Tombstoned);
        }

        var state = await GetApplicationAsync(tenantId, applicationId, cancellationToken);
        if (state.IsDeleted)
        {
            return await ProjectCurrentAsync(message, state, cancellationToken);
        }

        var submission = await ProjectSubmissionAsync(
            tenantId,
            applicationId,
            new SubmissionTarget(
                message.SubmissionId!.Value,
                message.ResponseId!.Value,
                message.SourceRevision,
                message.OccurredAt,
                message.TemplateId ?? state.TemplateId,
                message.TemplateVersionId ?? state.TemplateVersionId),
            cancellationToken);

        var current = await ProjectCurrentAsync(message, state, cancellationToken);
        return submission.Status == ProjectionStatus.Projected ? submission : current;
    }

    private async Task<ProjectionOutcome> ProjectSubmissionAsync(Guid tenantId, Guid applicationId, SubmissionTarget target, CancellationToken cancellationToken)
    {
        var policy = await store.GetExportPolicyAsync(tenantId, target.TemplateId, cancellationToken);
        var versions = VersionsFor(policy);

        var existing = await store.GetSubmissionAsync(tenantId, target.SubmissionId, cancellationToken);
        if (existing is not null && existing.Versions.CompareTo(versions) >= 0)
        {
            return ProjectionOutcome.Skipped(ProjectionReasons.AlreadyProjected);
        }

        var response = await TimeSourceAsync("get_response", () => source.GetResponseAsync(tenantId, target.ResponseId, cancellationToken));
        if (response.ApplicationId != applicationId)
        {
            throw new PermanentProjectionException(
                PermanentFailureReasons.SourceMismatch,
                $"Response {target.ResponseId} belongs to application {response.ApplicationId}, not {applicationId}.");
        }

        var flattened = await FlattenAsync(tenantId, target.TemplateVersionId, response.ResponseBody, policy, cancellationToken);
        var projection = new SubmissionProjection(
            tenantId,
            applicationId,
            target.SubmissionId,
            target.SourceRevision,
            response.ResponseId,
            response.CreatedAtRevision,
            target.SubmittedAt,
            target.TemplateId,
            target.TemplateVersionId,
            flattened.Hash,
            versions);

        var written = await TimeWriteAsync("write_submission", () => writer.WriteSubmissionAsync(projection, flattened.Facts, cancellationToken));
        return Created(nameof(GenerationKind.Submission), written);
    }

    private async Task<ProjectionOutcome> RecordDeletionAsync(Guid tenantId, Guid applicationId, long sourceRevision, DateTime deletedAt, CancellationToken cancellationToken)
    {
        var result = await TimeWriteAsync(
            "record_deletion",
            () => writer.RecordDeletionAsync(new DeletionRecord(tenantId, applicationId, sourceRevision, deletedAt), cancellationToken));

        return result.Outcome == WriteOutcome.Applied
            ? ProjectionOutcome.Projected(ProjectionReasons.Deleted, result.GenerationId)
            : ProjectionOutcome.Skipped(ProjectionReasons.AlreadyProjected);
    }

    private async Task<Flattened> FlattenAsync(Guid tenantId, Guid templateVersionId, string body, ExportPolicy policy, CancellationToken cancellationToken)
    {
        var template = await TimeSourceAsync("get_template_version", () => source.GetTemplateVersionAsync(tenantId, templateVersionId, cancellationToken));
        var catalogue = catalogues.GetOrBuild(tenantId, templateVersionId, template.JsonSchema);
        await EnsureCatalogueAsync(tenantId, template, catalogue, policy, force: false, cancellationToken);

        var started = clock.GetTimestamp();
        var result = ResponseFlattener.Flatten(catalogue, ResponseParser.Parse(body), policy);
        var hash = FactHasher.Compute(
            new FactHashHeader(templateVersionId, PrismVersions.ProjectorVersion, PrismVersions.ContractVersion, policy.Version),
            result.Facts);
        metrics.Flattened(clock.GetElapsedTime(started));

        if (result.Warnings.Count > 0)
        {
            LogFlattenWarnings(result.Warnings.Count, string.Join(", ", result.Warnings.GroupBy(w => w.Code).Select(g => $"{g.Key}={g.Count()}")));
        }

        return new Flattened(result.Facts, hash);
    }

    private async Task EnsureCatalogueAsync(
        Guid tenantId,
        PrismTemplateVersionDto template,
        TemplateCatalogue catalogue,
        ExportPolicy policy,
        bool force,
        CancellationToken cancellationToken)
    {
        var templateVersionId = template.TemplateVersionId;
        if (!force && catalogues.IsEnsured(tenantId, templateVersionId, PrismVersions.ContractVersion, policy.Version))
        {
            return;
        }

        await catalogWriter.EnsureAsync(
            new CatalogueSource(tenantId, template.TemplateId, templateVersionId, template.VersionNumber, template.CreatedOn),
            catalogue,
            policy,
            cancellationToken);
        catalogues.MarkEnsured(tenantId, templateVersionId, PrismVersions.ContractVersion, policy.Version);
    }

    private Task<PrismApplicationStateDto> GetApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
        => TimeSourceAsync("get_application", () => source.GetApplicationAsync(tenantId, applicationId, cancellationToken));

    private ProjectionOutcome Created(string kind, WriteResult result)
    {
        if (result.Outcome != WriteOutcome.Applied)
        {
            return Rejected(result);
        }

        metrics.GenerationCreated(kind, result.SupersededGenerationId is not null);
        return ProjectionOutcome.Projected(ProjectionReasons.NewGeneration, result.GenerationId);
    }

    private static ProjectionOutcome Rejected(WriteResult result) => ProjectionOutcome.Skipped(
        result.Outcome == WriteOutcome.Tombstoned ? ProjectionReasons.Tombstoned : ProjectionReasons.CasConflict);

    private static ProjectionVersions VersionsFor(ExportPolicy policy)
        => new(PrismVersions.ProjectorVersion, PrismVersions.ContractVersion, policy.Version);

    private Task<T> TimeSourceAsync<T>(string operation, Func<Task<T>> call)
        => PrismMetrics.TimeAsync(call, elapsed => metrics.SourceCall(operation, elapsed));

    private Task<WriteResult> TimeWriteAsync(string operation, Func<Task<WriteResult>> write)
        => PrismMetrics.TimeAsync(write, elapsed => metrics.Written(operation, elapsed));

    [LoggerMessage(Level = LogLevel.Information, Message = "Projection {Status}: {Outcome} (generation {GenerationId})")]
    private partial void LogProjected(ProjectionStatus status, string outcome, Guid? generationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Template version catalogued with {FieldCount} fields")]
    private partial void LogTemplateCatalogued(int fieldCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flattening reported {WarningCount} warnings: {WarningSummary}")]
    private partial void LogFlattenWarnings(int warningCount, string warningSummary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Projection failed permanently ({FailureReason})")]
    private partial void LogPermanentFailure(Exception exception, string failureReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Projection failed and will be retried")]
    private partial void LogTransientFailure(Exception exception);

    private sealed record Flattened(IReadOnlyList<AnswerFact> Facts, byte[] Hash);

    private sealed record SubmissionTarget(
        Guid SubmissionId,
        Guid ResponseId,
        long SourceRevision,
        DateTime SubmittedAt,
        Guid TemplateId,
        Guid TemplateVersionId);
}
