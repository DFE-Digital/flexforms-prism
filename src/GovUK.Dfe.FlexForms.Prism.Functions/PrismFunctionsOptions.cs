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

    /// <summary>
    /// Tenants Prism never reports on, such as test and Playwright tenants. Their messages are completed without
    /// projecting, and backfills and reconciliations skip them. Data already projected for them is not removed.
    /// </summary>
    public Guid[] ExcludedTenantIds { get; set; } = [];

    public bool IsExcluded(Guid tenantId) => ExcludedTenantIds.Contains(tenantId);

    public BackfillOptions Backfill { get; set; } = new();

    public ProjectionOptions Projection { get; set; } = new();

    public CleanupOptions Cleanup { get; set; } = new();

    public AdminAuthOptions Admin { get; set; } = new();
}

public sealed class BackfillOptions
{
    /// <summary>Applications requested from the source per page.</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>How long one worker run may page before stopping and resuming on the next tick.</summary>
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>
    /// The most Resync messages the worker enqueues per minute, sent in small slices every few seconds, so a large
    /// backfill drains steadily instead of landing on the database at once. Zero means no limit.
    /// </summary>
    public int MaxMessagesPerMinute { get; set; } = 240;
}

public sealed class ProjectionOptions
{
    /// <summary>
    /// How long a transient failure waits before the message is abandoned, doubled on each delivery up to
    /// <see cref="MaxRetryDelay"/>. Without it redeliveries are immediate and use up <c>MaxDeliveryCount</c> in seconds
    /// while the database is still overloaded.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RetryDelayFor(int deliveryCount)
    {
        var doublings = Math.Clamp(deliveryCount - 1, 0, 30);
        return TimeSpan.FromTicks((long)Math.Min(RetryDelay.Ticks * Math.Pow(2, doublings), MaxRetryDelay.Ticks));
    }
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

    /// <summary>
    /// Lets a trusted caller, such as the FlexForms API, name the person it acts for in the
    /// <c>X-Prism-Acting-User</c> header, so the audit trail records them.
    /// </summary>
    public string DelegateRole { get; set; } = "Prism.Delegate";

    /// <summary>
    /// A shared key accepted in the <c>X-Prism-Development-Key</c> header instead of a token, with both roles. Only
    /// honoured when the Functions environment is Development, for running the FlexForms API against Prism locally.
    /// </summary>
    public string? DevelopmentKey { get; set; }
}

public static class PrismTopology
{
    public const string TopicName = "flexforms-prism";
    public const string SubscriptionName = "prism-projector";
    public const string ServiceBusConnection = "ServiceBus";
}
