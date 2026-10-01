using System.Diagnostics.Metrics;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Maintenance;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Functions;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Projector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

public sealed record FactRow(string FieldId, string OccurrencePath, string? Value);

public sealed record FieldChangeRow(
    string ParentFieldId,
    string FieldId,
    string ChangeType,
    Guid? PreviousTemplateVersionId,
    string? PreviousLabel,
    string? Label,
    bool LabelChanged,
    bool TypeChanged,
    bool RequiredChanged,
    bool ChoicesChanged,
    bool LocationChanged,
    string ExportDecision);

/// <summary>
/// The real projector, writer, store and control plane over SQL Server, fed by <see cref="FakeSource"/>. Each
/// delivery gets its own DbContext, like a function invocation, and goes through the wire format.
/// </summary>
internal sealed class ScenarioHarness
{
    public const string Template = """
        {
          "templateId": "tpl", "templateName": "Scenario", "description": "",
          "taskGroups": [{
            "groupId": "g1", "groupName": "Group", "groupOrder": 1, "groupStatus": "NotStarted",
            "tasks": [
              {
                "taskId": "about", "taskName": "About", "taskOrder": 1, "taskStatus": "NotStarted",
                "pages": [{
                  "pageId": "p1", "slug": "p1", "title": "About", "description": "", "pageOrder": 1,
                  "fields": [
                    { "fieldId": "name", "type": "text", "label": { "value": "Name" }, "order": 1 },
                    { "fieldId": "pupils", "type": "number", "label": { "value": "Pupils" }, "order": 2 },
                    { "fieldId": "secret", "type": "text", "label": { "value": "Unclassified field" }, "order": 3 }
                  ]
                }]
              },
              {
                "taskId": "people", "taskName": "People", "taskOrder": 2, "taskStatus": "NotStarted",
                "summary": {
                  "mode": "multiCollectionFlow",
                  "flows": [{
                    "flowId": "members", "title": "Members", "fieldId": "members",
                    "pages": [{
                      "pageId": "m1", "slug": "m1", "title": "Member", "description": "", "pageOrder": 1,
                      "fields": [
                        { "fieldId": "memberName", "type": "text", "label": { "value": "Member name" }, "order": 1 },
                        { "fieldId": "joined", "type": "date", "label": { "value": "Joined" }, "order": 2 }
                      ]
                    }]
                  }]
                }
              }
            ]
          }]
        }
        """;

    private readonly SqlServerFixture sql;
    private readonly CatalogueCache catalogues = new();
    private readonly TestMeterFactory meters = new();

    public ScenarioHarness(SqlServerFixture sql)
    {
        this.sql = sql;
        Source = new FakeSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Template);
    }

    public FakeSource Source { get; }

    public Guid TenantId => Source.TenantId;

    /// <summary>Allows the scenario fields, leaving <c>secret</c> without a decision unless <paramref name="allowSecret"/>.</summary>
    public async Task ClassifyAsync(int policyVersion = 1, bool allowSecret = false)
    {
        List<(string Parent, string Field)> allowed = [("", "name"), ("", "pupils"), ("", "members"), ("members", "memberName"), ("members", "joined")];
        if (allowSecret)
        {
            allowed.Add(("", "secret"));
        }

        await ClearPolicyAsync();
        await using var db = sql.CreateContext();
        db.FieldExportPolicies.AddRange(allowed.Select(a => new FieldExportPolicy
        {
            TenantId = TenantId,
            TemplateId = Source.TemplateId,
            ParentFieldId = a.Parent,
            FieldId = a.Field,
            Decision = ExportDecision.Allowed,
            PolicyVersion = policyVersion,
            DecidedBy = "scenario",
            DecidedAt = DateTime.UtcNow,
        }));
        await db.SaveChangesAsync();
    }

    /// <summary>Changes the policy the way the admin endpoint does.</summary>
    public async Task<ApplyResult> ChangePolicyAsync(params ExportDecisionRequest[] decisions)
    {
        await using var db = sql.CreateContext();
        return await new ExportPolicyService(db, TimeProvider.System).ApplyAsync(TenantId, Source.TemplateId, decisions, "scenario", default);
    }

    public async Task ClearPolicyAsync()
    {
        await using var db = sql.CreateContext();
        await db.FieldExportPolicies.Where(p => p.TenantId == TenantId).ExecuteDeleteAsync();
    }

    /// <summary>Delivers a message the way the function would, through the Service Bus wire format.</summary>
    public async Task<ProjectionOutcome> DeliverAsync(ApplicationProjectionRequestedEvent message)
    {
        var received = (ApplicationProjectionRequestedEvent)ProjectionMessages.Read(ProjectionMessages.Create(message).Body);
        await using var db = sql.CreateContext();
        return await CreateService(db).ProjectAsync(received, CancellationToken.None);
    }

    /// <summary>Delivers a template event the way the function would, through the Service Bus wire format.</summary>
    public async Task DeliverAsync(TemplateVersionPublishedEvent message)
    {
        var received = (TemplateVersionPublishedEvent)ProjectionMessages.Read(ProjectionMessages.Create(message).Body);
        await using var db = sql.CreateContext();
        await CreateService(db).CatalogueTemplateVersionAsync(received, CancellationToken.None);
    }

    public async Task<List<FieldChangeRow>> FieldChangesAsync(Guid templateVersionId)
    {
        await using var db = sql.CreateContext();
        return await db.Database
            .SqlQuery<FieldChangeRow>($"""
                SELECT parent_field_id AS ParentFieldId, field_id AS FieldId, change_type AS ChangeType,
                       previous_template_version_id AS PreviousTemplateVersionId, previous_label AS PreviousLabel, label AS Label,
                       label_changed AS LabelChanged, type_changed AS TypeChanged, required_changed AS RequiredChanged,
                       choices_changed AS ChoicesChanged, location_changed AS LocationChanged, export_decision AS ExportDecision
                FROM prism.v_template_field_changes
                WHERE tenant_id = {TenantId} AND template_version_id = {templateVersionId}
                ORDER BY parent_field_id, field_id
                """)
            .ToListAsync();
    }

    private ProjectionService CreateService(PrismDbContext db) => new(
        Source, new ProjectionStore(db), new ProjectionWriter(db, TimeProvider.System), new FieldCatalogWriter(db, TimeProvider.System),
        catalogues, new PrismMetrics(meters), TimeProvider.System, NullLogger<ProjectionService>.Instance);

    /// <summary>Runs a backfill or reconciliation to completion and delivers every message it enqueued.</summary>
    public async Task<(OperationView Operation, IReadOnlyList<ApplicationProjectionRequestedEvent> Enqueued)> RunOperationAsync(OperationKind kind)
    {
        var bus = new CapturingSender();
        await using (var db = sql.CreateContext())
        {
            var created = await new OperationService(db, TimeProvider.System).CreateAsync(kind, TenantId, null, "scenario", default);
            var options = Options.Create(new PrismFunctionsOptions { Backfill = { PageSize = 2 } });
            await new OperationProcessor(
                    db, Source, new ProjectionStore(db), bus, new ControlPlaneMetrics(meters), TimeProvider.System, options,
                    NullLogger<OperationProcessor>.Instance)
                .RunAsync(default);

            foreach (var message in bus.Sent)
            {
                await DeliverAsync(message);
            }

            return ((await new OperationService(db, TimeProvider.System).GetAsync(created.OperationId, default))!, bus.Sent);
        }
    }

    public async Task<CleanupResult> CleanupAsync(DateTime supersededBefore)
    {
        await using var db = sql.CreateContext();
        return await new GenerationCleaner(db).DeleteSupersededAsync(supersededBefore, 10_000, default);
    }

    public async Task<List<FactRow>> CurrentFactsAsync(Guid applicationId)
    {
        await using var db = sql.CreateContext();
        return await db.Database
            .SqlQuery<FactRow>($"""
                SELECT field_id AS FieldId, occurrence_path AS OccurrencePath, value_string AS Value
                FROM prism.v_current_answer_facts
                WHERE tenant_id = {TenantId} AND application_id = {applicationId}
                ORDER BY occurrence_path, field_id
                """)
            .ToListAsync();
    }

    public async Task<List<FactRow>> SubmissionFactsAsync(Guid submissionId)
    {
        await using var db = sql.CreateContext();
        return await db.Database
            .SqlQuery<FactRow>($"""
                SELECT field_id AS FieldId, occurrence_path AS OccurrencePath, value_string AS Value
                FROM prism.v_submission_answer_facts
                WHERE tenant_id = {TenantId} AND submission_id = {submissionId}
                ORDER BY occurrence_path, field_id
                """)
            .ToListAsync();
    }

    public async Task<ApplicationProjectionState?> StateAsync(Guid applicationId)
    {
        await using var db = sql.CreateContext();
        return await db.ApplicationProjectionStates.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == TenantId && s.ApplicationId == applicationId);
    }

    public async Task<List<ProjectionGeneration>> GenerationsAsync(Guid applicationId)
    {
        await using var db = sql.CreateContext();
        return await db.ProjectionGenerations.AsNoTracking()
            .Where(g => g.TenantId == TenantId && g.ApplicationId == applicationId)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> FactCountAsync(Guid generationId)
    {
        await using var db = sql.CreateContext();
        return await db.AnswerFacts.CountAsync(f => f.GenerationId == generationId);
    }

    public async Task<List<FieldCatalogEntry>> CatalogAsync()
    {
        await using var db = sql.CreateContext();
        return await db.FieldCatalog.AsNoTracking()
            .Where(c => c.TenantId == TenantId && c.TemplateVersionId == Source.TemplateVersionId)
            .OrderBy(c => c.FieldOrder)
            .ToListAsync();
    }

    public async Task ExecuteSqlAsync(FormattableString sql)
    {
        await using var db = this.sql.CreateContext();
        await db.Database.ExecuteSqlAsync(sql);
    }

    /// <summary>
    /// Installs a trigger that fails any state write for applications listed in <c>scenario.crash_points</c>.
    /// The writer updates the state after bulk-copying facts, so this simulates a crash between the two.
    /// </summary>
    public async Task InstallCrashTriggerAsync()
    {
        await using var db = sql.CreateContext();
        await db.Database.ExecuteSqlRawAsync("IF SCHEMA_ID('scenario') IS NULL EXEC('CREATE SCHEMA scenario');");
        await db.Database.ExecuteSqlRawAsync(
            "IF OBJECT_ID('scenario.crash_points') IS NULL CREATE TABLE scenario.crash_points (application_id uniqueidentifier PRIMARY KEY);");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR ALTER TRIGGER prism.tr_scenario_crash ON prism.application_projection_state AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted i JOIN scenario.crash_points c ON c.application_id = i.application_id)
                    THROW 50001, 'Simulated crash after bulk copy', 1;
            END
            """);
    }

    private sealed class CapturingSender : IProjectionRequestSender
    {
        private readonly List<ApplicationProjectionRequestedEvent> sent = [];

        public IReadOnlyList<ApplicationProjectionRequestedEvent> Sent => sent;

        public Task SendAsync(IReadOnlyCollection<ApplicationProjectionRequestedEvent> messages, CancellationToken cancellationToken)
        {
            sent.AddRange(messages);
            return Task.CompletedTask;
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options.Name, options.Version, options.Tags, this);

        public void Dispose()
        {
        }
    }
}
