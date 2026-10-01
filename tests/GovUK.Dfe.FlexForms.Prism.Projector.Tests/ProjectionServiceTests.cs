using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Enums;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Source;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GovUK.Dfe.FlexForms.Prism.Projector.Tests;

public class ProjectionServiceTests
{
    private readonly ProjectorHarness h = new();

    [Fact]
    public async Task Saved_for_a_new_application_writes_a_current_generation()
    {
        var state = h.State(3, ProjectorHarness.Body("Ada"));
        h.SourceReturns(state);

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 3), default);

        Assert.Equal(ProjectionOutcome.Projected(ProjectionReasons.NewGeneration, outcome.GenerationId), outcome);
        await h.Writer.Received(1).WriteCurrentAsync(
            Arg.Is<CurrentProjection>(p =>
                p.SourceRevision == 3
                && p.ResponseId == state.ResponseId
                && p.Lifecycle == ApplicationLifecycle.Draft
                && p.Versions == new ProjectionVersions(1, 1, 1)
                && p.SourceHash.Length == 32),
            Arg.Is<IReadOnlyCollection<AnswerFact>>(facts => facts.Count == 1 && facts.First().FieldId == "name" && facts.First().ValueString == "Ada"),
            Arg.Any<CancellationToken>());
        await h.CatalogWriter.Received(1).EnsureAsync(
            Arg.Is<CatalogueSource>(s => s.TemplateVersionId == h.TemplateVersionId && s.TemplateVersionNumber == "1.0"),
            Arg.Any<TemplateCatalogue>(), Arg.Any<ExportPolicy>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saved_at_or_below_the_stored_revision_is_skipped_without_calling_the_source()
    {
        h.Stored(5);

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 4), default);

        Assert.Equal(ProjectionOutcome.Skipped(ProjectionReasons.AlreadyProjected), outcome);
        await h.Source.DidNotReceiveWithAnyArgs().GetApplicationAsync(default, default, default);
    }

    [Fact]
    public async Task A_newer_export_policy_reprojects_the_same_revision()
    {
        h.Stored(5, policyVersion: 1);
        h.Store.GetExportPolicyAsync(h.TenantId, h.TemplateId, Arg.Any<CancellationToken>()).Returns(ProjectorHarness.Policy(2));
        h.SourceReturns(h.State(5, ProjectorHarness.Body("Ada")));

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 5), default);

        Assert.Equal(ProjectionStatus.Projected, outcome.Status);
        await h.Writer.Received(1).WriteCurrentAsync(
            Arg.Is<CurrentProjection>(p => p.Versions.ExportPolicyVersion == 2), Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_source_state_that_is_not_newer_than_the_stored_one_is_skipped()
    {
        h.Stored(7);
        h.SourceReturns(h.State(7, ProjectorHarness.Body("Ada")));

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Resync, 8), default);

        Assert.Equal(ProjectionOutcome.Skipped(ProjectionReasons.NotNewer), outcome);
        await h.Writer.DidNotReceiveWithAnyArgs().WriteCurrentAsync(default!, default!, default);
    }

    [Fact]
    public async Task Unchanged_facts_advance_the_state_without_a_new_generation()
    {
        CurrentProjection? first = null;
        h.Writer.WriteCurrentAsync(Arg.Do<CurrentProjection>(p => first = p), Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>())
            .Returns(new WriteResult(WriteOutcome.Applied, Guid.NewGuid()));
        h.SourceReturns(h.State(3, ProjectorHarness.Body("Ada")));
        await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 3), default);

        h.Writer.ClearReceivedCalls();
        h.Stored(3, hash: first!.SourceHash);
        h.SourceReturns(h.State(4, ProjectorHarness.Body("Ada")));
        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 4), default);

        Assert.Equal(ProjectionReasons.HashReused, outcome.Reason);
        await h.Writer.Received(1).AdvanceCurrentAsync(Arg.Is<CurrentProjection>(p => p.SourceRevision == 4), Arg.Any<CancellationToken>());
        await h.Writer.DidNotReceiveWithAnyArgs().WriteCurrentAsync(default!, default!, default);
    }

    [Fact]
    public async Task The_catalogue_is_ensured_once_per_template_version_and_policy_version()
    {
        h.SourceReturns(h.State(3, ProjectorHarness.Body("Ada")));
        await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 3), default);
        h.SourceReturns(h.State(4, ProjectorHarness.Body("Grace")));
        await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 4), default);

        await h.CatalogWriter.ReceivedWithAnyArgs(1).EnsureAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_tombstoned_application_is_skipped_without_calling_the_source()
    {
        h.Store.IsTombstonedAsync(h.TenantId, h.ApplicationId, Arg.Any<CancellationToken>()).Returns(true);

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 3), default);

        Assert.Equal(ProjectionOutcome.Skipped(ProjectionReasons.Tombstoned), outcome);
        await h.Source.DidNotReceiveWithAnyArgs().GetApplicationAsync(default, default, default);
    }

    [Fact]
    public async Task A_source_that_reports_the_application_deleted_records_a_deletion()
    {
        h.SourceReturns(h.State(9, status: ApplicationStatus.Deleted));

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 8), default);

        Assert.Equal(ProjectionReasons.Deleted, outcome.Reason);
        await h.Writer.Received(1).RecordDeletionAsync(Arg.Is<DeletionRecord>(d => d.SourceRevision == 9), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_lost_compare_and_swap_is_reported_as_a_skip()
    {
        h.SourceReturns(h.State(3, ProjectorHarness.Body("Ada")));
        h.Writer.WriteCurrentAsync(Arg.Any<CurrentProjection>(), Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>())
            .Returns(WriteResult.Stale);

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 3), default);

        Assert.Equal(ProjectionOutcome.Skipped(ProjectionReasons.CasConflict), outcome);
    }

    [Fact]
    public async Task An_application_without_a_response_is_skipped()
    {
        h.SourceReturns(h.State(1, body: null));

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Resync, 1), default);

        Assert.Equal(ProjectionOutcome.Skipped(ProjectionReasons.NoResponse), outcome);
    }

    [Fact]
    public async Task Submitted_freezes_the_submitted_response_and_updates_current()
    {
        var responseId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        h.SourceReturns(h.State(4, ProjectorHarness.Body("Ada"), ApplicationStatus.Submitted, responseId, 4, submissionId, responseId));
        h.Source.GetResponseAsync(h.TenantId, responseId, Arg.Any<CancellationToken>())
            .Returns(new PrismResponseDto(responseId, h.ApplicationId, 3, h.OccurredAt, ProjectorHarness.Body("Ada")));

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Submitted, 4, responseId, submissionId), default);

        Assert.Equal(ProjectionReasons.NewGeneration, outcome.Reason);
        await h.Writer.Received(1).WriteSubmissionAsync(
            Arg.Is<SubmissionProjection>(p =>
                p.SubmissionId == submissionId
                && p.ResponseId == responseId
                && p.ResponseRevision == 3
                && p.SourceRevision == 4
                && p.SubmittedAt == h.OccurredAt),
            Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>());
        await h.Writer.Received(1).WriteCurrentAsync(
            Arg.Is<CurrentProjection>(p => p.Lifecycle == ApplicationLifecycle.Submitted && p.SourceRevision == 4),
            Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submitted_skips_a_snapshot_already_projected_at_these_versions()
    {
        var submissionId = Guid.NewGuid();
        h.SourceReturns(h.State(4, ProjectorHarness.Body("Ada"), ApplicationStatus.Submitted));
        h.Store.GetSubmissionAsync(h.TenantId, submissionId, Arg.Any<CancellationToken>())
            .Returns(new StoredSubmission(Guid.NewGuid(), 4, new ProjectionVersions(1, 1, 1)));

        await h.Service.ProjectAsync(h.Message(ProjectionReason.Submitted, 4, submissionId: submissionId), default);

        await h.Source.DidNotReceiveWithAnyArgs().GetResponseAsync(default, default, default);
        await h.Writer.DidNotReceiveWithAnyArgs().WriteSubmissionAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_submitted_response_from_another_application_fails_permanently()
    {
        var responseId = Guid.NewGuid();
        h.SourceReturns(h.State(4, ProjectorHarness.Body("Ada"), ApplicationStatus.Submitted));
        h.Source.GetResponseAsync(h.TenantId, responseId, Arg.Any<CancellationToken>())
            .Returns(new PrismResponseDto(responseId, Guid.NewGuid(), 3, h.OccurredAt, "{}"));

        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.ProjectAsync(h.Message(ProjectionReason.Submitted, 4, responseId), default));

        Assert.Equal(PermanentFailureReasons.SourceMismatch, ex.Reason);
    }

    [Fact]
    public async Task Resync_creates_a_missing_submission_snapshot()
    {
        var responseId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        h.Stored(4);
        h.SourceReturns(h.State(4, ProjectorHarness.Body("Ada"), ApplicationStatus.Submitted, responseId, 4, submissionId, responseId));
        h.Source.GetResponseAsync(h.TenantId, responseId, Arg.Any<CancellationToken>())
            .Returns(new PrismResponseDto(responseId, h.ApplicationId, 3, h.OccurredAt, ProjectorHarness.Body("Ada")));
        h.Store.GetExportPolicyAsync(h.TenantId, h.TemplateId, Arg.Any<CancellationToken>()).Returns(ProjectorHarness.Policy(1));

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Resync, 5), default);

        Assert.Equal(ProjectionStatus.Projected, outcome.Status);
        await h.Writer.Received(1).WriteSubmissionAsync(
            Arg.Is<SubmissionProjection>(p => p.SubmissionId == submissionId && p.SourceRevision == 4),
            Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleted_records_a_tombstone_at_the_event_revision()
    {
        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Deleted, 6), default);

        Assert.Equal(ProjectionReasons.Deleted, outcome.Reason);
        await h.Writer.Received(1).RecordDeletionAsync(
            Arg.Is<DeletionRecord>(d => d.SourceRevision == 6 && d.DeletedAt == h.OccurredAt), Arg.Any<CancellationToken>());
        await h.Source.DidNotReceiveWithAnyArgs().GetApplicationAsync(default, default, default);
    }

    [Fact]
    public async Task An_older_deletion_is_skipped()
    {
        h.Writer.RecordDeletionAsync(Arg.Any<DeletionRecord>(), Arg.Any<CancellationToken>()).Returns(WriteResult.Stale);

        var outcome = await h.Service.ProjectAsync(h.Message(ProjectionReason.Deleted, 6), default);

        Assert.Equal(ProjectionOutcome.Skipped(ProjectionReasons.AlreadyProjected), outcome);
    }

    [Fact]
    public async Task An_unsupported_contract_version_fails_permanently()
    {
        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 1, contractVersion: 2), default));

        Assert.Equal(PermanentFailureReasons.UnsupportedContract, ex.Reason);
    }

    [Fact]
    public async Task A_submitted_message_without_a_submission_id_fails_permanently()
    {
        var message = h.Message(ProjectionReason.Submitted, 1) with { SubmissionId = null };

        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(() => h.Service.ProjectAsync(message, default));

        Assert.Equal(PermanentFailureReasons.InvalidMessage, ex.Reason);
    }

    [Fact]
    public async Task A_missing_application_fails_permanently()
    {
        h.Source.GetApplicationAsync(h.TenantId, h.ApplicationId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SourceNotFoundException("gone"));

        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 1), default));

        Assert.Equal(PermanentFailureReasons.SourceNotFound, ex.Reason);
    }

    [Fact]
    public async Task An_unavailable_source_is_rethrown_for_retry()
    {
        h.Source.GetApplicationAsync(h.TenantId, h.ApplicationId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SourceUnavailableException("down", 503));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 1), default));
    }

    [Fact]
    public async Task An_unreadable_template_fails_permanently()
    {
        h.SourceReturns(h.State(1, ProjectorHarness.Body("Ada")));
        h.Source.GetTemplateVersionAsync(h.TenantId, h.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(new PrismTemplateVersionDto(h.TemplateVersionId, h.TemplateId, "1.0", "not json", h.OccurredAt));

        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 1), default));

        Assert.Equal(PermanentFailureReasons.UnreadableTemplate, ex.Reason);
    }

    [Fact]
    public async Task An_unreadable_response_fails_permanently()
    {
        h.SourceReturns(h.State(1, "[1, 2"));

        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 1), default));

        Assert.Equal(PermanentFailureReasons.UnreadableResponse, ex.Reason);
    }

    [Fact]
    public async Task A_published_template_version_is_catalogued_with_its_source_creation_time_and_policy()
    {
        await h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(), default);

        await h.CatalogWriter.Received(1).EnsureAsync(
            new CatalogueSource(h.TenantId, h.TemplateId, h.TemplateVersionId, "1.0", h.OccurredAt),
            Arg.Is<TemplateCatalogue>(c => c.Fields.Select(f => f.FieldId).SequenceEqual(new[] { "name", "pupils" })),
            Arg.Is<ExportPolicy>(p => p.Version == 1),
            Arg.Any<CancellationToken>());
        await h.Writer.DidNotReceiveWithAnyArgs().WriteCurrentAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_redelivered_template_version_is_catalogued_again()
    {
        await h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(), default);
        await h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(), default);

        await h.CatalogWriter.Received(2).EnsureAsync(
            Arg.Any<CatalogueSource>(), Arg.Any<TemplateCatalogue>(), Arg.Any<ExportPolicy>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Projecting_after_the_template_event_reuses_the_catalogue()
    {
        await h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(), default);
        h.SourceReturns(h.State(1, ProjectorHarness.Body("Ada")));

        await h.Service.ProjectAsync(h.Message(ProjectionReason.Saved, 1), default);

        await h.CatalogWriter.Received(1).EnsureAsync(
            Arg.Any<CatalogueSource>(), Arg.Any<TemplateCatalogue>(), Arg.Any<ExportPolicy>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_template_version_that_belongs_to_another_template_fails_permanently()
    {
        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(templateId: Guid.NewGuid()), default));

        Assert.Equal(PermanentFailureReasons.SourceMismatch, ex.Reason);
        await h.CatalogWriter.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task An_unsupported_template_event_contract_fails_permanently()
    {
        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(contractVersion: 2), default));

        Assert.Equal(PermanentFailureReasons.UnsupportedContract, ex.Reason);
    }

    [Fact]
    public async Task An_unreadable_published_template_fails_permanently()
    {
        h.Source.GetTemplateVersionAsync(h.TenantId, h.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(new PrismTemplateVersionDto(h.TemplateVersionId, h.TemplateId, "1.0", "not json", h.OccurredAt));

        var ex = await Assert.ThrowsAsync<PermanentProjectionException>(
            () => h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(), default));

        Assert.Equal(PermanentFailureReasons.UnreadableTemplate, ex.Reason);
    }

    [Fact]
    public async Task An_unavailable_source_while_cataloguing_is_rethrown_for_retry()
    {
        h.Source.GetTemplateVersionAsync(h.TenantId, h.TemplateVersionId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SourceUnavailableException("down", 503));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => h.Service.CatalogueTemplateVersionAsync(h.TemplatePublished(), default));
    }
}
