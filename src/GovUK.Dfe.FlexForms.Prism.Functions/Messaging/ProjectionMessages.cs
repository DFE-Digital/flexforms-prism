using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Identifiers;
using GovUK.Dfe.FlexForms.Prism.Projector;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Messaging;

/// <summary>
/// Reads and writes <see cref="ApplicationProjectionRequestedEvent"/> in the MassTransit envelope the API
/// publishes, so live events and control-plane Resync messages look the same on the topic.
/// </summary>
public static class ProjectionMessages
{
    public const string MassTransitContentType = "application/vnd.masstransit+json";

    public static readonly string MessageType =
        $"urn:message:{typeof(ApplicationProjectionRequestedEvent).Namespace}:{nameof(ApplicationProjectionRequestedEvent)}";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the event from a MassTransit envelope, or from a bare event body. Throws
    /// <see cref="PermanentProjectionException"/> when the body can never be read.
    /// </summary>
    public static ApplicationProjectionRequestedEvent Read(BinaryData body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The message body is not a JSON object.");
            }

            var payload = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object ? message : root;
            return payload.Deserialize<ApplicationProjectionRequestedEvent>(ReadOptions)
                   ?? throw Invalid("The message body is empty.");
        }
        catch (JsonException ex)
        {
            throw Invalid($"The message body is not a valid projection request: {ex.Message}", ex);
        }
    }

    /// <summary>A Service Bus message carrying the event, with the session and deterministic message ids set.</summary>
    public static ServiceBusMessage Create(ApplicationProjectionRequestedEvent message)
    {
        var messageId = ApplicationProjectionIdentifiers.MessageId(
            message.TenantId, message.ApplicationId, message.SourceRevision, message.Reason, message.OperationId);

        var envelope = new Envelope(
            messageId,
            message.OperationId ?? messageId,
            [MessageType],
            message,
            DateTime.UtcNow,
            new Dictionary<string, object>());

        return new ServiceBusMessage(BinaryData.FromObjectAsJson(envelope, WriteOptions))
        {
            MessageId = messageId.ToString("D"),
            SessionId = ApplicationProjectionIdentifiers.SessionId(message.TenantId, message.ApplicationId),
            ContentType = MassTransitContentType,
            CorrelationId = message.OperationId?.ToString("D"),
        };
    }

    private static PermanentProjectionException Invalid(string message, Exception? inner = null)
        => new(PermanentFailureReasons.InvalidMessage, message, inner);

    private sealed record Envelope(
        Guid MessageId,
        Guid ConversationId,
        string[] MessageType,
        ApplicationProjectionRequestedEvent Message,
        DateTime SentTime,
        Dictionary<string, object> Headers);
}
