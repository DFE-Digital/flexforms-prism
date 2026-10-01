using Azure.Messaging.ServiceBus;

namespace GovUK.Dfe.FlexForms.Prism.Functions;

/// <summary>Bound from the <c>Prism</c> configuration section.</summary>
public sealed class PrismFunctionsOptions
{
    public const string SectionName = "Prism";

    /// <summary>Topic the control plane enqueues Resync messages onto. The trigger uses the same literal name.</summary>
    public string TopicName { get; set; } = PrismTopology.TopicName;

    /// <summary>AMQP over WebSockets by default, matching the other platform services on this network.</summary>
    public ServiceBusTransportType ServiceBusTransport { get; set; } = ServiceBusTransportType.AmqpWebSockets;

    public BackfillOptions Backfill { get; set; } = new();

    public CleanupOptions Cleanup { get; set; } = new();

    public AdminAuthOptions Admin { get; set; } = new();
}

public sealed class BackfillOptions
{
    /// <summary>Applications requested from the source per page.</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>How long one worker run may page before stopping and resuming on the next tick.</summary>
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromMinutes(4);
}

public sealed class CleanupOptions
{
    /// <summary>How long superseded generations are kept before they may be deleted.</summary>
    public int RetentionDays { get; set; } = 30;

    public int MaxGenerationsPerRun { get; set; } = 2000;
}

/// <summary>Entra ID settings for the admin endpoints.</summary>
public sealed class AdminAuthOptions
{
    /// <summary>For example <c>https://login.microsoftonline.com/{tenant}/v2.0</c>. Unset means every request is rejected.</summary>
    public string? Authority { get; set; }

    /// <summary>The Prism app registration's application ID URI or client id.</summary>
    public string? Audience { get; set; }

    /// <summary>Extra accepted issuers, for example the v1 <c>https://sts.windows.net/{tenant}/</c> issuer.</summary>
    public string[] ValidIssuers { get; set; } = [];

    public string RequiredRole { get; set; } = "Prism.Admin";
}

public static class PrismTopology
{
    public const string TopicName = "flexforms-prism";
    public const string SubscriptionName = "prism-projector";
    public const string ServiceBusConnection = "ServiceBus";
}
