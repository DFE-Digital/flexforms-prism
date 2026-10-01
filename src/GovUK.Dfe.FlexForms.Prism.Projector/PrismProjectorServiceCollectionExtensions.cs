using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GovUK.Dfe.FlexForms.Prism.Projector;

public static class PrismProjectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProjectionService"/>. The data and source registrations, logging and metrics are
    /// expected to be added by the host.
    /// </summary>
    public static IServiceCollection AddPrismProjector(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CatalogueCache>();
        services.TryAddSingleton<PrismMetrics>();
        services.TryAddScoped<IProjectionService, ProjectionService>();
        return services;
    }
}
