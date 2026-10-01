using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GovUK.Dfe.FlexForms.Prism.Data;

public static class PrismDataServiceCollectionExtensions
{
    public static IServiceCollection AddPrismData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<PrismDbContext>(options => ConfigureSqlServer(options, connectionString));
        services.AddScoped<IProjectionWriter, ProjectionWriter>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    public static DbContextOptionsBuilder ConfigureSqlServer(DbContextOptionsBuilder options, string connectionString)
        => options.UseSqlServer(connectionString, SqlServerOptions);

    private static void SqlServerOptions(SqlServerDbContextOptionsBuilder sql)
        => sql.MigrationsHistoryTable("__EFMigrationsHistory", PrismDbContext.Schema);
}
