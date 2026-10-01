using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Projector;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;

namespace GovUK.Dfe.FlexForms.Prism.Functions;

public static class PrismFunctionsServiceCollectionExtensions
{
    private const string ApplicationInsightsConnectionString = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>
    /// Registers everything the Prism functions need. The SQL connection string is <c>ConnectionStrings:Prism</c>;
    /// use <c>Authentication=Active Directory Default</c> for managed identity. Service Bus uses the same
    /// <c>ServiceBus</c> connection as the trigger: a connection string, or <c>ServiceBus:fullyQualifiedNamespace</c>
    /// (plus <c>ServiceBus:clientId</c> for a user-assigned identity).
    /// </summary>
    public static IServiceCollection AddPrismFunctions(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Prism")
            ?? throw new InvalidOperationException("ConnectionStrings:Prism is not configured.");

        services.AddOptions<PrismFunctionsOptions>().Bind(configuration.GetSection(PrismFunctionsOptions.SectionName));
        services.AddMetrics();
        AddMetricsExport(services, configuration);

        services.AddPrismData(connectionString);
        services.AddPrismSource(configuration);
        services.AddPrismProjector();

        services.AddSingleton(provider => CreateServiceBusClient(configuration, provider.GetRequiredService<IOptions<PrismFunctionsOptions>>().Value));
        services.AddSingleton<IProjectionRequestSender, ProjectionRequestSender>();
        services.AddSingleton<IAdminAuthenticator, AdminAuthenticator>();
        services.AddSingleton<ControlPlaneMetrics>();
        services.AddScoped<OperationService>();
        services.AddScoped<OperationProcessor>();
        services.AddScoped<ExportPolicyService>();
        return services;
    }

    /// <summary>
    /// The classic Application Insights SDK ignores <see cref="System.Diagnostics.Metrics"/> meters, so the Prism
    /// meter is exported with OpenTelemetry. The metrics land in the <c>customMetrics</c> table.
    /// </summary>
    private static void AddMetricsExport(IServiceCollection services, IConfiguration configuration)
    {
        if (configuration[ApplicationInsightsConnectionString] is not { Length: > 0 } connectionString)
        {
            return;
        }

        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(PrismMetrics.MeterName)
            .AddAzureMonitorMetricExporter(options => options.ConnectionString = connectionString));
    }

    private static ServiceBusClient CreateServiceBusClient(IConfiguration configuration, PrismFunctionsOptions options)
    {
        var clientOptions = new ServiceBusClientOptions { TransportType = options.ServiceBusTransport };
        var section = configuration.GetSection(PrismTopology.ServiceBusConnection);
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            return new ServiceBusClient(section.Value, clientOptions);
        }

        var fullyQualifiedNamespace = section["fullyQualifiedNamespace"]
            ?? throw new InvalidOperationException("Configure ServiceBus (connection string) or ServiceBus:fullyQualifiedNamespace.");
        TokenCredential credential = section["clientId"] is { Length: > 0 } clientId
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new DefaultAzureCredential();
        return new ServiceBusClient(fullyQualifiedNamespace, credential, clientOptions);
    }
}
