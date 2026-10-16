using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class TenantDirectoryWriterTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Upserts_tenant_names_and_refreshes_them_on_a_later_call()
    {
        var tenantId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await using (var db = sql.CreateContext())
        {
            await new TenantDirectoryWriter(db, TimeProvider.System).UpsertAsync(
                [new TenantInfo(tenantId, "Academies"), new TenantInfo(otherId, "Free schools")],
                default);
        }

        await using (var db = sql.CreateContext())
        {
            var rows = await db.Tenants.AsNoTracking()
                .Where(t => t.TenantId == tenantId || t.TenantId == otherId)
                .OrderBy(t => t.TenantName)
                .ToListAsync();
            Assert.Equal(["Academies", "Free schools"], rows.Select(t => t.TenantName));
        }

        await using (var db = sql.CreateContext())
        {
            await new TenantDirectoryWriter(db, TimeProvider.System).UpsertAsync(
                [new TenantInfo(tenantId, "Academies (renamed)")],
                default);
        }

        await using (var db = sql.CreateContext())
        {
            Assert.Equal(
                "Academies (renamed)",
                await db.Tenants.AsNoTracking().Where(t => t.TenantId == tenantId).Select(t => t.TenantName).SingleAsync());
        }
    }
}
