using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GovUK.Dfe.FlexForms.Prism.Data;

public static class PrismDataServiceCollectionExtensions
{
    public static IServiceCollection AddPrismData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<PrismDbContext>(options => ConfigureSqlServer(options, connectionString));
        services.AddScoped<IProjectionWriter, ProjectionWriter>();
        services.AddScoped<IProjectionStore, ProjectionStore>();
        services.AddScoped<IFieldCatalogWriter, FieldCatalogWriter>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static DbContextOptionsBuilder ConfigureSqlServer(DbContextOptionsBuilder options, string connectionString)
        => options.UseSqlServer(connectionString, SqlServerOptions);

    private static void SqlServerOptions(SqlServerDbContextOptionsBuilder sql)
        => sql.MigrationsHistoryTable("__EFMigrationsHistory", PrismDbContext.Schema);
}
