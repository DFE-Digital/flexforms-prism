using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Projector;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GovUK.Dfe.FlexForms.Prism.Functions;

public static class PrismFunctionsServiceCollectionExtensions
{
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

        services.AddPrismData(connectionString);
        services.AddPrismSource(configuration);
        services.AddPrismProjector();

        services.AddSingleton(provider => CreateServiceBusClient(configuration, provider.GetRequiredService<IOptions<PrismFunctionsOptions>>().Value));
        services.AddSingleton<IProjectionRequestSender, ProjectionRequestSender>();
        services.AddSingleton<IAdminAuthenticator, AdminAuthenticator>();
        services.AddSingleton<ControlPlaneMetrics>();
        services.AddScoped<OperationService>();
        services.AddScoped<OperationProcessor>();
        return services;
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
