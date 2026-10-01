using System.Text.Json;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Enums;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Identifiers;
using GovUK.Dfe.FlexForms.Prism.Source;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>
/// An in-memory FlexForms for one tenant. Each transition bumps the application revision and returns the event
/// the API would publish for it, so tests decide when, how often and in what order events are delivered.
/// </summary>
internal sealed class FakeSource : ISourceClient
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Application> applications = [];
    private readonly Dictionary<Guid, PrismResponseDto> responses = [];
    private readonly Dictionary<Guid, PrismTemplateVersionDto> templates = [];

    public FakeSource(Guid tenantId, Guid templateId, Guid templateVersionId, string templateJson)
    {
        TenantId = tenantId;
        TemplateId = templateId;
        TemplateVersionId = templateVersionId;
        templates[templateVersionId] = new PrismTemplateVersionDto(templateVersionId, templateId, "1.0", templateJson, DateTime.UtcNow);
    }

    /// <summary>The event the API publishes for the template version the source was created with.</summary>
    public TemplateVersionPublishedEvent InitialTemplateVersion() => Published(templates[TemplateVersionId]);

    /// <summary>Adds a newer version of the template and returns the event the API would publish for it.</summary>
    public TemplateVersionPublishedEvent PublishTemplateVersion(string versionNumber, string templateJson)
    {
        lock (gate)
        {
            var createdOn = templates.Values.Max(t => t.CreatedOn).AddMinutes(1);
            var version = new PrismTemplateVersionDto(Guid.NewGuid(), TemplateId, versionNumber, templateJson, createdOn);
            templates[version.TemplateVersionId] = version;
            return Published(version);
        }
    }

    private TemplateVersionPublishedEvent Published(PrismTemplateVersionDto version) => new(
        TemplateVersionPublishedEvent.CurrentContractVersion, TenantId, version.TemplateId, version.TemplateVersionId,
        version.VersionNumber, version.CreatedOn);

    public Guid TenantId { get; }
    public Guid TemplateId { get; }
    public Guid TemplateVersionId { get; }

    /// <summary>Rewrites what GetApplicationAsync returns, to simulate stale or racing reads.</summary>
    public Func<PrismApplicationStateDto, PrismApplicationStateDto>? ApplicationView { get; set; }

    public static string Body(params (string FieldId, string Value)[] answers) =>
        JsonSerializer.Serialize(answers.ToDictionary(
            a => a.FieldId,
            a => new { question = "", value = a.Value, completed = true, dataType = "string" }));

    public ApplicationProjectionRequestedEvent Save(Guid applicationId, string body)
    {
        lock (gate)
        {
            var application = GetOrCreate(applicationId);
            application.Revision++;
            var response = new PrismResponseDto(Guid.NewGuid(), applicationId, application.Revision, DateTime.UtcNow, body);
            responses[response.ResponseId] = response;
            application.Responses.Add(response);
            application.LastModifiedOn = DateTime.UtcNow;
            return Event(application, ProjectionReason.Saved, response.ResponseId, null);
        }
    }

    public ApplicationProjectionRequestedEvent Submit(Guid applicationId)
    {
        lock (gate)
        {
            var application = applications[applicationId];
            application.Revision++;
            application.Status = ApplicationStatus.Submitted;
            application.SubmittedRevision = application.Revision;
            application.LastModifiedOn = DateTime.UtcNow;
            return Event(
                application,
                ProjectionReason.Submitted,
                application.Responses[^1].ResponseId,
                ApplicationProjectionIdentifiers.SubmissionId(applicationId, application.Revision));
        }
    }

    public ApplicationProjectionRequestedEvent Delete(Guid applicationId)
    {
        lock (gate)
        {
            var application = applications[applicationId];
            application.Revision++;
            application.Status = ApplicationStatus.Deleted;
            application.DeletedOn = DateTime.UtcNow;
            return Event(application, ProjectionReason.Deleted, null, null);
        }
    }

    public Task<IReadOnlyList<PrismTenantDto>> GetTenantsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PrismTenantDto>>([new PrismTenantDto(TenantId, "Scenario tenant")]);

    public Task<PrismApplicationStateDto> GetApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (tenantId != TenantId || !applications.TryGetValue(applicationId, out var application))
            {
                throw new SourceNotFoundException($"No application {applicationId}.");
            }

            var state = StateAt(application, application.Revision);
            return Task.FromResult(ApplicationView?.Invoke(state) ?? state);
        }
    }

    /// <summary>The application as the source would have returned it when it was at <paramref name="revision"/>.</summary>
    public PrismApplicationStateDto Snapshot(Guid applicationId, long revision)
    {
        lock (gate)
        {
            return StateAt(applications[applicationId], revision);
        }
    }

    private PrismApplicationStateDto StateAt(Application application, long revision)
    {
        var latest = application.Responses.LastOrDefault(r => r.CreatedAtRevision <= revision);
        var submittedRevision = application.SubmittedRevision <= revision ? application.SubmittedRevision : null;
        var deleted = application.Status == ApplicationStatus.Deleted && revision == application.Revision;
        var status = deleted ? ApplicationStatus.Deleted
            : submittedRevision is not null ? ApplicationStatus.Submitted
            : ApplicationStatus.InProgress;
        var submitted = submittedRevision is { } s ? application.Responses.Last(r => r.CreatedAtRevision <= s) : null;

        return new PrismApplicationStateDto(
            application.Id, $"APP-{application.Id.ToString()[..8]}", revision, status, deleted, deleted ? application.DeletedOn : null,
            TemplateId, TemplateVersionId, application.CreatedOn, application.LastModifiedOn,
            latest?.ResponseId, latest?.CreatedAtRevision, latest?.ResponseBody,
            submittedRevision,
            submittedRevision is { } r ? ApplicationProjectionIdentifiers.SubmissionId(application.Id, r) : null,
            submitted?.ResponseId);
    }

    public Task<PrismResponseDto> GetResponseAsync(Guid tenantId, Guid responseId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return responses.TryGetValue(responseId, out var response)
                ? Task.FromResult(response)
                : throw new SourceNotFoundException($"No response {responseId}.");
        }
    }

    public Task<PrismTemplateVersionDto> GetTemplateVersionAsync(Guid tenantId, Guid templateVersionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(templates[templateVersionId]);
        }
    }

    public Task<PrismApplicationPageDto> ListApplicationsAsync(Guid tenantId, DateTime? modifiedSince, int page, int pageSize, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var all = applications.Values.OrderBy(a => a.CreatedOn).ThenBy(a => a.Id).ToList();
            var items = all.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(a => new PrismApplicationSummaryDto(a.Id, a.Revision, a.Status, a.Status == ApplicationStatus.Deleted, a.LastModifiedOn ?? a.CreatedOn))
                .ToList();
            return Task.FromResult(new PrismApplicationPageDto(items, page, pageSize, page * pageSize < all.Count));
        }
    }

    private Application GetOrCreate(Guid applicationId)
    {
        if (!applications.TryGetValue(applicationId, out var application))
        {
            application = new Application { Id = applicationId, CreatedOn = DateTime.UtcNow };
            applications[applicationId] = application;
        }

        return application;
    }

    private ApplicationProjectionRequestedEvent Event(Application application, ProjectionReason reason, Guid? responseId, Guid? submissionId) => new(
        ApplicationProjectionRequestedEvent.CurrentContractVersion, TenantId, application.Id, reason, application.Revision,
        responseId, submissionId, TemplateId, TemplateVersionId, null, DateTime.UtcNow);

    private sealed class Application
    {
        public Guid Id { get; init; }
        public long Revision { get; set; }
        public ApplicationStatus Status { get; set; } = ApplicationStatus.InProgress;
        public long? SubmittedRevision { get; set; }
        public DateTime CreatedOn { get; init; }
        public DateTime? LastModifiedOn { get; set; }
        public DateTime? DeletedOn { get; set; }
        public List<PrismResponseDto> Responses { get; } = [];
    }
}
