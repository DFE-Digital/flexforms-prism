using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

/// <summary>
/// One SQL Server container per test run with the Prism migrations applied. Tests isolate themselves by
/// using fresh tenant and application IDs rather than resetting the database.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "prism" }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public PrismDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrismDbContext>();
        PrismDataServiceCollectionExtensions.ConfigureSqlServer(options, ConnectionString);
        return new PrismDbContext(options.Options);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
