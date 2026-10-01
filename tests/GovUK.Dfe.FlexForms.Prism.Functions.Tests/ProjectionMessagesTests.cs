using System.Text.Json;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Identifiers;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Projector;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

public class ProjectionMessagesTests
{
    [Fact]
    public void A_created_message_round_trips_and_carries_the_session_and_deterministic_id()
    {
        var request = TestSupport.Event(ProjectionReason.Resync, 7, Guid.NewGuid());

        var message = ProjectionMessages.Create(request);

        Assert.Equal(request, ProjectionMessages.Read(message.Body));
        Assert.Equal(ApplicationProjectionIdentifiers.SessionId(request.TenantId, request.ApplicationId), message.SessionId);
        Assert.Equal(
            ApplicationProjectionIdentifiers.MessageId(request.TenantId, request.ApplicationId, 7, ProjectionReason.Resync, request.OperationId).ToString("D"),
            message.MessageId);
        Assert.Equal(ProjectionMessages.MassTransitContentType, message.ContentType);

        using var envelope = JsonDocument.Parse(message.Body);
        Assert.Equal(ProjectionMessages.MessageType, envelope.RootElement.GetProperty("messageType")[0].GetString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"Submitted\"")]
    public void Reads_a_MassTransit_envelope_with_numeric_or_named_reasons(string reason)
    {
        var tenant = Guid.NewGuid();
        var body = $$"""
            {
              "messageId": "{{Guid.NewGuid()}}",
              "messageType": ["urn:message:GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events:ApplicationProjectionRequestedEvent"],
              "message": {
                "contractVersion": 1, "tenantId": "{{tenant}}", "applicationId": "{{Guid.NewGuid()}}", "reason": {{reason}},
                "sourceRevision": 4, "responseId": "{{Guid.NewGuid()}}", "submissionId": "{{Guid.NewGuid()}}",
                "occurredAt": "2026-09-30T12:00:00Z"
              }
            }
            """;

        var request = Assert.IsType<ApplicationProjectionRequestedEvent>(ProjectionMessages.Read(BinaryData.FromString(body)));

        Assert.Equal(ProjectionReason.Submitted, request.Reason);
        Assert.Equal(tenant, request.TenantId);
        Assert.Equal(4, request.SourceRevision);
        Assert.Null(request.OperationId);
    }

    [Fact]
    public void Reads_a_bare_event_body()
    {
        var request = TestSupport.Event();

        Assert.Equal(request, ProjectionMessages.Read(BinaryData.FromObjectAsJson(request)));
    }

    [Fact]
    public void A_template_version_message_round_trips_on_the_template_session()
    {
        var published = TestSupport.TemplateVersionPublished();

        var message = ProjectionMessages.Create(published);

        Assert.Equal(published, ProjectionMessages.Read(message.Body));
        Assert.Equal(ApplicationProjectionIdentifiers.TemplateSessionId(published.TenantId, published.TemplateId), message.SessionId);
        Assert.Equal(
            ApplicationProjectionIdentifiers.TemplateVersionMessageId(published.TenantId, published.TemplateVersionId).ToString("D"),
            message.MessageId);

        using var envelope = JsonDocument.Parse(message.Body);
        Assert.Equal(ProjectionMessages.TemplateVersionMessageType, envelope.RootElement.GetProperty("messageType")[0].GetString());
    }

    [Fact]
    public void Reads_a_template_version_envelope_as_the_API_publishes_it()
    {
        var (tenant, template, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var body = $$"""
            {
              "messageId": "{{Guid.NewGuid()}}",
              "messageType": [
                "urn:message:GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events:TemplateVersionPublishedEvent"
              ],
              "message": {
                "contractVersion": 1, "tenantId": "{{tenant}}", "templateId": "{{template}}", "templateVersionId": "{{version}}",
                "versionNumber": "1.4.0", "createdAt": "2026-09-30T12:00:00Z"
              }
            }
            """;

        var published = Assert.IsType<TemplateVersionPublishedEvent>(ProjectionMessages.Read(BinaryData.FromString(body)));

        Assert.Equal((tenant, template, version, "1.4.0"), (published.TenantId, published.TemplateId, published.TemplateVersionId, published.VersionNumber));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "message": { "sourceRevision": "many" } }""")]
    public void Unreadable_bodies_are_permanent_failures(string body)
    {
        var ex = Assert.Throws<PermanentProjectionException>(() => ProjectionMessages.Read(BinaryData.FromString(body)));

        Assert.Equal(PermanentFailureReasons.InvalidMessage, ex.Reason);
    }
}
