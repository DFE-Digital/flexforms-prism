using GovUK.Dfe.FlexForms.Api.Client;
using GovUK.Dfe.FlexForms.Api.Client.Contracts;
using GovUK.Dfe.FlexForms.Api.Client.Extensions;
using GovUK.Dfe.FlexForms.Api.Client.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GovUK.Dfe.FlexForms.Prism.Source;

public static class PrismSourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISourceClient"/> over the FlexForms API client, configured from the
    /// <c>ExternalApplicationsApiClient</c> section and authenticating with client credentials.
    /// </summary>
    public static IServiceCollection AddPrismSource(this IServiceCollection services, IConfiguration configuration)
    {
        // Must be registered before the API client, which only adds its own provider when none exists.
        var settings = new ConfigurationApiClientSettingsProvider(configuration).GetSettings();
        services.AddSingleton<IApiClientSettingsProvider>(new TenantScopedApiClientSettingsProvider(settings));
        services.AddExternalApplicationsApiClient<IInternalPrismClient, InternalPrismClient>(configuration, enableTokenExchange: false);

        services.AddSingleton(new SourceResilience());
        services.AddSingleton<TemplateVersionCache>();
        services.AddTransient<ISourceClient, SourceClient>();
        return services;
    }
}
