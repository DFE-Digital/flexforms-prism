using Azure.Messaging.ServiceBus;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using Microsoft.Extensions.Options;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Messaging;

public interface IProjectionRequestSender
{
    Task SendAsync(IReadOnlyCollection<ApplicationProjectionRequestedEvent> messages, CancellationToken cancellationToken);
}

public sealed class ProjectionRequestSender : IProjectionRequestSender, IAsyncDisposable
{
    private readonly ServiceBusSender sender;

    public ProjectionRequestSender(ServiceBusClient client, IOptions<PrismFunctionsOptions> options)
    {
        sender = client.CreateSender(options.Value.TopicName);
    }

    public async Task SendAsync(IReadOnlyCollection<ApplicationProjectionRequestedEvent> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var batch = await sender.CreateMessageBatchAsync(cancellationToken);
        try
        {
            foreach (var message in messages)
            {
                var serviceBusMessage = ProjectionMessages.Create(message);
                if (batch.TryAddMessage(serviceBusMessage))
                {
                    continue;
                }

                await sender.SendMessagesAsync(batch, cancellationToken);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(cancellationToken);
                if (!batch.TryAddMessage(serviceBusMessage))
                {
                    throw new InvalidOperationException($"Message {serviceBusMessage.MessageId} is too large for an empty batch.");
                }
            }

            await sender.SendMessagesAsync(batch, cancellationToken);
        }
        finally
        {
            batch.Dispose();
        }
    }

    public ValueTask DisposeAsync() => sender.DisposeAsync();
}
