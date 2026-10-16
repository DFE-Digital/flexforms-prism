using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Catalog;

/// <summary>A tenant id and display name from the FlexForms tenants list.</summary>
public sealed record TenantInfo(Guid TenantId, string TenantName);

public interface ITenantDirectoryWriter
{
    /// <summary>Inserts or refreshes each tenant's display name.</summary>
    Task UpsertAsync(IEnumerable<TenantInfo> tenants, CancellationToken cancellationToken);
}

public sealed class TenantDirectoryWriter(PrismDbContext db, TimeProvider clock) : ITenantDirectoryWriter
{
    private const int NameMaxLength = 200;

    public async Task UpsertAsync(IEnumerable<TenantInfo> tenants, CancellationToken cancellationToken)
    {
        var list = tenants
            .GroupBy(t => t.TenantId)
            .Select(g => g.Last())
            .ToList();
        if (list.Count == 0)
        {
            return;
        }

        var ids = list.Select(t => t.TenantId).ToList();
        var existing = await db.Tenants
            .Where(t => ids.Contains(t.TenantId))
            .ToDictionaryAsync(t => t.TenantId, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var tenant in list)
        {
            var name = Truncate(tenant.TenantName, NameMaxLength) ?? string.Empty;
            if (existing.TryGetValue(tenant.TenantId, out var entry))
            {
                entry.TenantName = name;
                entry.UpdatedAt = now;
                continue;
            }

            db.Tenants.Add(new TenantEntry
            {
                TenantId = tenant.TenantId,
                TenantName = name,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
