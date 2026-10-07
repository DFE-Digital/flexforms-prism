using Azure.Messaging.ServiceBus;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Functions.Functions;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Projector;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

public class ProjectionFunctionTests
{
    private readonly IProjectionService projector = Substitute.For<IProjectionService>();
    private readonly ServiceBusMessageActions actions = Substitute.For<ServiceBusMessageActions>();
    private readonly PrismFunctionsOptions options = new();
    private readonly ProjectionFunction function;

    public ProjectionFunctionTests()
    {
        function = new ProjectionFunction(
            projector, new PrismMetrics(new TestMeterFactory()), Options.Create(options), NullLogger<ProjectionFunction>.Instance);
    }

    private static ServiceBusReceivedMessage Received(BinaryData body, int deliveryCount = 1) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(body: body, messageId: Guid.NewGuid().ToString(), sessionId: "t:a", deliveryCount: deliveryCount);

    [Fact]
    public async Task A_projected_message_is_completed()
    {
        var request = TestSupport.Event();
        var message = Received(ProjectionMessages.Create(request).Body);
        projector.ProjectAsync(request, Arg.Any<CancellationToken>()).Returns(ProjectionOutcome.Skipped(ProjectionReasons.AlreadyProjected));

        await function.Run(message, actions, default);

        await projector.Received(1).ProjectAsync(request, Arg.Any<CancellationToken>());
        await actions.Received(1).CompleteMessageAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_template_version_message_is_catalogued_and_completed()
    {
        var published = TestSupport.TemplateVersionPublished();
        var message = Received(ProjectionMessages.Create(published).Body);

        await function.Run(message, actions, default);

        await projector.Received(1).CatalogueTemplateVersionAsync(published, Arg.Any<CancellationToken>());
        await projector.DidNotReceiveWithAnyArgs().ProjectAsync(default!, default);
        await actions.Received(1).CompleteMessageAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_message_for_an_excluded_tenant_is_completed_without_projecting()
    {
        var request = TestSupport.Event();
        options.ExcludedTenantIds = [request.TenantId];
        var message = Received(ProjectionMessages.Create(request).Body);

        await function.Run(message, actions, default);

        await projector.DidNotReceiveWithAnyArgs().ProjectAsync(default!, default);
        await actions.Received(1).CompleteMessageAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_template_version_for_an_excluded_tenant_is_completed_without_cataloguing()
    {
        var published = TestSupport.TemplateVersionPublished();
        options.ExcludedTenantIds = [published.TenantId];
        var message = Received(ProjectionMessages.Create(published).Body);

        await function.Run(message, actions, default);

        await projector.DidNotReceiveWithAnyArgs().CatalogueTemplateVersionAsync(default!, default);
        await actions.Received(1).CompleteMessageAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_permanent_template_failure_is_dead_lettered()
    {
        var message = Received(ProjectionMessages.Create(TestSupport.TemplateVersionPublished()).Body);
        projector.CatalogueTemplateVersionAsync(Arg.Any<TemplateVersionPublishedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PermanentProjectionException(PermanentFailureReasons.UnreadableTemplate, "bad"));

        await function.Run(message, actions, default);

        await actions.Received(1).DeadLetterMessageAsync(
            message, Arg.Any<Dictionary<string, object>>(), PermanentFailureReasons.UnreadableTemplate, "bad", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_permanent_failure_is_dead_lettered_with_its_reason()
    {
        var message = Received(ProjectionMessages.Create(TestSupport.Event()).Body);
        projector.ProjectAsync(Arg.Any<ApplicationProjectionRequestedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PermanentProjectionException(PermanentFailureReasons.SourceNotFound, "gone"));

        await function.Run(message, actions, default);

        await actions.Received(1).DeadLetterMessageAsync(
            message,
            Arg.Is<Dictionary<string, object>>(p => (string)p["PrismFailure"] == PermanentFailureReasons.SourceNotFound),
            PermanentFailureReasons.SourceNotFound,
            "gone",
            Arg.Any<CancellationToken>());
        await actions.DidNotReceiveWithAnyArgs().CompleteMessageAsync(default!, default);
    }

    [Fact]
    public async Task An_unreadable_body_is_dead_lettered_without_projecting()
    {
        var message = Received(BinaryData.FromString("not json"));

        await function.Run(message, actions, default);

        await projector.DidNotReceiveWithAnyArgs().ProjectAsync(default!, default);
        await actions.Received(1).DeadLetterMessageAsync(
            message, Arg.Any<Dictionary<string, object>>(), PermanentFailureReasons.InvalidMessage, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_failure_is_abandoned_and_rethrown()
    {
        var message = Received(ProjectionMessages.Create(TestSupport.Event()).Body, deliveryCount: 2);
        projector.ProjectAsync(Arg.Any<ApplicationProjectionRequestedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SourceUnavailableException("down", 503));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => function.Run(message, actions, default));

        await actions.Received(1).AbandonMessageAsync(message, Arg.Any<IDictionary<string, object>>(), Arg.Any<CancellationToken>());
        await actions.DidNotReceiveWithAnyArgs().CompleteMessageAsync(default!, default);
    }
}
