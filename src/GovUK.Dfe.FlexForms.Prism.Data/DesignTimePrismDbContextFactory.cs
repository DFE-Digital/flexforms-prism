using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GovUK.Dfe.FlexForms.Prism.Data;

/// <summary>
/// Used by <c>dotnet ef</c> to create migrations and bundles. The bundle receives the real connection
/// string through <c>--connection</c>.
/// </summary>
public sealed class DesignTimePrismDbContextFactory : IDesignTimeDbContextFactory<PrismDbContext>
{
    public PrismDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PRISM_DB_CONNECTION")
            ?? "Server=localhost;Database=prism;Integrated Security=true;TrustServerCertificate=true";

        var options = new DbContextOptionsBuilder<PrismDbContext>();
        PrismDataServiceCollectionExtensions.ConfigureSqlServer(options, connectionString);
        return new PrismDbContext(options.Options);
    }
}
