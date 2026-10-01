using Azure.Messaging.ServiceBus;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Projector;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Functions;

/// <summary>
/// Projects one application per message. Sessions keep every message for an application in order. Messages are
/// completed on success or skip, dead-lettered on permanent failures, and abandoned on transient failures so
/// Service Bus redelivers them until <c>MaxDeliveryCount</c>.
/// </summary>
public sealed partial class ProjectionFunction(IProjectionService projector, PrismMetrics metrics, ILogger<ProjectionFunction> logger)
{
    private const int MaxDeadLetterDescriptionLength = 1024;

    [Function(nameof(ProjectionFunction))]
    public async Task Run(
        [ServiceBusTrigger(PrismTopology.TopicName, PrismTopology.SubscriptionName, Connection = PrismTopology.ServiceBusConnection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["message_id"] = message.MessageId,
            ["session_id"] = message.SessionId,
            ["delivery_count"] = message.DeliveryCount,
        });

        try
        {
            var request = ProjectionMessages.Read(message.Body);
            if (message.DeliveryCount > 1)
            {
                metrics.Retried(request.Reason);
            }

            await projector.ProjectAsync(request, cancellationToken);
            await actions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (PermanentProjectionException ex)
        {
            LogDeadLettering(ex.Reason);
            await actions.DeadLetterMessageAsync(
                message,
                new Dictionary<string, object> { ["PrismFailure"] = ex.Reason },
                ex.Reason,
                Truncate(ex.Message, MaxDeadLetterDescriptionLength),
                CancellationToken.None);
        }
        catch (Exception)
        {
            await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
            throw;
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettering message: {FailureReason}")]
    private partial void LogDeadLettering(string failureReason);
}
