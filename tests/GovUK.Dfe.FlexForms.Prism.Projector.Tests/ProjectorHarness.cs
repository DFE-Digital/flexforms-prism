using System.Diagnostics.Metrics;
using System.Text.Json;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Enums;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GovUK.Dfe.FlexForms.Prism.Projector.Tests;

/// <summary>A projection service over substitutes, with one application and one template version.</summary>
internal sealed class ProjectorHarness
{
    public const string Template = """
        {
          "templateId": "tpl", "templateName": "Sample", "description": "",
          "taskGroups": [{
            "groupId": "g1", "groupName": "Group", "groupOrder": 1, "groupStatus": "NotStarted",
            "tasks": [{
              "taskId": "about", "taskName": "About", "taskOrder": 1, "taskStatus": "NotStarted",
              "pages": [{
                "pageId": "p1", "slug": "p1", "title": "Page one", "description": "", "pageOrder": 1,
                "fields": [
                  { "fieldId": "name", "type": "text", "label": { "value": "Name" }, "order": 1 },
                  { "fieldId": "pupils", "type": "number", "label": { "value": "Pupils" }, "order": 2 }
                ]
              }]
            }]
          }]
        }
        """;

    public readonly Guid TenantId = Guid.NewGuid();
    public readonly Guid ApplicationId = Guid.NewGuid();
    public readonly Guid TemplateId = Guid.NewGuid();
    public readonly Guid TemplateVersionId = Guid.NewGuid();
    public readonly DateTime OccurredAt = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public ISourceClient Source { get; } = Substitute.For<ISourceClient>();
    public IProjectionStore Store { get; } = Substitute.For<IProjectionStore>();
    public IProjectionWriter Writer { get; } = Substitute.For<IProjectionWriter>();
    public IFieldCatalogWriter CatalogWriter { get; } = Substitute.For<IFieldCatalogWriter>();
    public ProjectionService Service { get; }

    public ProjectorHarness()
    {
        Service = new ProjectionService(
            Source, Store, Writer, CatalogWriter, new CatalogueCache(), new PrismMetrics(new TestMeterFactory()),
            TimeProvider.System, NullLogger<ProjectionService>.Instance);

        Store.GetExportPolicyAsync(TenantId, TemplateId, Arg.Any<CancellationToken>()).Returns(Policy(1));
        Source.GetTemplateVersionAsync(TenantId, TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(new PrismTemplateVersionDto(TemplateVersionId, TemplateId, "1.0", Template, OccurredAt));

        Writer.WriteCurrentAsync(Arg.Any<CurrentProjection>(), Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>())
            .Returns(new WriteResult(WriteOutcome.Applied, Guid.NewGuid()));
        Writer.AdvanceCurrentAsync(Arg.Any<CurrentProjection>(), Arg.Any<CancellationToken>())
            .Returns(new WriteResult(WriteOutcome.Applied, Guid.NewGuid()));
        Writer.WriteSubmissionAsync(Arg.Any<SubmissionProjection>(), Arg.Any<IReadOnlyCollection<AnswerFact>>(), Arg.Any<CancellationToken>())
            .Returns(new WriteResult(WriteOutcome.Applied, Guid.NewGuid()));
        Writer.RecordDeletionAsync(Arg.Any<DeletionRecord>(), Arg.Any<CancellationToken>())
            .Returns(new WriteResult(WriteOutcome.Applied));
    }

    public static ExportPolicy Policy(int version) => new(version, [new ExportRule("", "name", ExportDecision.Allowed)]);

    public static string Body(string name) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = new { question = "", value = name, completed = true, dataType = "string" },
        });

    public ApplicationProjectionRequestedEvent Message(
        ProjectionReason reason,
        long revision,
        Guid? responseId = null,
        Guid? submissionId = null,
        int contractVersion = ApplicationProjectionRequestedEvent.CurrentContractVersion) =>
        new(contractVersion, TenantId, ApplicationId, reason, revision,
            responseId ?? (reason is ProjectionReason.Saved or ProjectionReason.Submitted ? Guid.NewGuid() : null),
            submissionId ?? (reason == ProjectionReason.Submitted ? Guid.NewGuid() : null),
            TemplateId, TemplateVersionId, reason == ProjectionReason.Resync ? Guid.NewGuid() : null, OccurredAt);

    public PrismApplicationStateDto State(
        long revision,
        string? body = "{}",
        ApplicationStatus status = ApplicationStatus.InProgress,
        Guid? responseId = null,
        long? submittedRevision = null,
        Guid? submissionId = null,
        Guid? submittedResponseId = null) =>
        new(ApplicationId, "APP-1", revision, status, status == ApplicationStatus.Deleted, status == ApplicationStatus.Deleted ? OccurredAt : null,
            TemplateId, TemplateVersionId, OccurredAt.AddDays(-1), OccurredAt,
            body is null ? null : responseId ?? Guid.NewGuid(), body is null ? null : revision, body,
            submittedRevision, submissionId, submittedResponseId);

    public void SourceReturns(PrismApplicationStateDto state) =>
        Source.GetApplicationAsync(TenantId, ApplicationId, Arg.Any<CancellationToken>()).Returns(state);

    public void Stored(long revision, int policyVersion = 1, byte[]? hash = null, Guid? activeGenerationId = null) =>
        Store.GetStateAsync(TenantId, ApplicationId, Arg.Any<CancellationToken>()).Returns(new StoredProjectionState(
            revision, Data.Entities.ApplicationLifecycle.Draft, activeGenerationId ?? Guid.NewGuid(), hash ?? [1, 2, 3], TemplateId,
            new ProjectionVersions(1, 1, policyVersion)));

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, this);
            meters.Add(meter);
            return meter;
        }

        public void Dispose() => meters.ForEach(m => m.Dispose());
    }
}
