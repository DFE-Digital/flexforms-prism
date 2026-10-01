using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Identifiers;
using GovUK.Dfe.FlexForms.Prism.Projector;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Messaging;

/// <summary>
/// Reads and writes the messages on the Prism topic in the MassTransit envelope the API publishes, so live events
/// and control-plane Resync messages look the same. The topic carries <see cref="ApplicationProjectionRequestedEvent"/>
/// and <see cref="TemplateVersionPublishedEvent"/>, told apart by the envelope's <c>messageType</c>.
/// </summary>
public static class ProjectionMessages
{
    public const string MassTransitContentType = "application/vnd.masstransit+json";

    public static readonly string MessageType = UrnFor<ApplicationProjectionRequestedEvent>();

    public static readonly string TemplateVersionMessageType = UrnFor<TemplateVersionPublishedEvent>();

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads an <see cref="ApplicationProjectionRequestedEvent"/> or a <see cref="TemplateVersionPublishedEvent"/>
    /// from a MassTransit envelope. A bare body is read as an application event. Throws
    /// <see cref="PermanentProjectionException"/> when the body can never be read.
    /// </summary>
    public static object Read(BinaryData body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The message body is not a JSON object.");
            }

            if (!root.TryGetProperty("message", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return Deserialize<ApplicationProjectionRequestedEvent>(root);
            }

            return IsTemplateVersion(root)
                ? Deserialize<TemplateVersionPublishedEvent>(payload)
                : Deserialize<ApplicationProjectionRequestedEvent>(payload);
        }
        catch (JsonException ex)
        {
            throw Invalid($"The message body is not a valid Prism message: {ex.Message}", ex);
        }
    }

    /// <summary>A Service Bus message carrying the event, with the session and deterministic message ids set.</summary>
    public static ServiceBusMessage Create(ApplicationProjectionRequestedEvent message)
    {
        var messageId = ApplicationProjectionIdentifiers.MessageId(
            message.TenantId, message.ApplicationId, message.SourceRevision, message.Reason, message.OperationId);

        return Create(
            new Envelope<ApplicationProjectionRequestedEvent>(
                messageId, message.OperationId ?? messageId, [MessageType], message, DateTime.UtcNow, []),
            ApplicationProjectionIdentifiers.SessionId(message.TenantId, message.ApplicationId),
            message.OperationId);
    }

    /// <summary>A Service Bus message carrying the template event, as the API publishes it.</summary>
    public static ServiceBusMessage Create(TemplateVersionPublishedEvent message)
    {
        var messageId = ApplicationProjectionIdentifiers.TemplateVersionMessageId(message.TenantId, message.TemplateVersionId);

        return Create(
            new Envelope<TemplateVersionPublishedEvent>(
                messageId, messageId, [TemplateVersionMessageType], message, DateTime.UtcNow, []),
            ApplicationProjectionIdentifiers.TemplateSessionId(message.TenantId, message.TemplateId),
            correlationId: null);
    }

    private static ServiceBusMessage Create<T>(Envelope<T> envelope, string sessionId, Guid? correlationId)
        => new(BinaryData.FromObjectAsJson(envelope, WriteOptions))
        {
            MessageId = envelope.MessageId.ToString("D"),
            SessionId = sessionId,
            ContentType = MassTransitContentType,
            CorrelationId = correlationId?.ToString("D"),
        };

    private static bool IsTemplateVersion(JsonElement envelope)
        => envelope.TryGetProperty("messageType", out var types)
           && types.ValueKind == JsonValueKind.Array
           && types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == TemplateVersionMessageType);

    private static T Deserialize<T>(JsonElement payload) where T : class
        => payload.Deserialize<T>(ReadOptions) ?? throw Invalid("The message body is empty.");

    private static string UrnFor<T>() => $"urn:message:{typeof(T).Namespace}:{typeof(T).Name}";

    private static PermanentProjectionException Invalid(string message, Exception? inner = null)
        => new(PermanentFailureReasons.InvalidMessage, message, inner);

    private sealed record Envelope<T>(
        Guid MessageId,
        Guid ConversationId,
        string[] MessageType,
        T Message,
        DateTime SentTime,
        Dictionary<string, object> Headers);
}
