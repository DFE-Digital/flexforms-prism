using System.Text.Json;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Catalog;

/// <summary>Identifies the template version a catalogue belongs to.</summary>
/// <param name="CreatedOn">When the version was created in FlexForms; orders the versions of a template.</param>
public sealed record CatalogueSource(Guid TenantId, Guid TemplateId, Guid TemplateVersionId, string? TemplateVersionNumber, DateTime CreatedOn);

public interface IFieldCatalogWriter
{
    /// <summary>
    /// Makes sure the template version and every one of its fields are catalogued for the current contract version,
    /// and that each entry's export status reflects <paramref name="policy"/>.
    /// </summary>
    Task EnsureAsync(CatalogueSource source, TemplateCatalogue catalogue, ExportPolicy policy, CancellationToken cancellationToken);
}

public sealed class FieldCatalogWriter(PrismDbContext db, TimeProvider clock) : IFieldCatalogWriter
{
    private const int UniqueKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public async Task EnsureAsync(CatalogueSource source, TemplateCatalogue catalogue, ExportPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await UpsertAsync(source, catalogue, policy, cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another session catalogued the same template version concurrently; its rows are now visible.
                db.ChangeTracker.Clear();
                await UpsertAsync(source, catalogue, policy, cancellationToken);
            }
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task UpsertAsync(CatalogueSource source, TemplateCatalogue catalogue, ExportPolicy policy, CancellationToken cancellationToken)
    {
        var existing = (await db.FieldCatalog
                .Where(c => c.TenantId == source.TenantId
                            && c.TemplateVersionId == source.TemplateVersionId
                            && c.ContractVersion == PrismVersions.ContractVersion)
                .ToListAsync(cancellationToken))
            .ToDictionary(c => (c.ParentFieldId, c.FieldId), KeyComparer.Instance);

        var now = clock.GetUtcNow().UtcDateTime;

        var version = await db.TemplateVersions.FindAsync([source.TenantId, source.TemplateVersionId], cancellationToken);
        if (version is null)
        {
            db.TemplateVersions.Add(new TemplateVersionEntry
            {
                TenantId = source.TenantId,
                TemplateVersionId = source.TemplateVersionId,
                TemplateId = source.TemplateId,
                VersionNumber = Truncate(source.TemplateVersionNumber, 50),
                CreatedOn = source.CreatedOn,
                CataloguedAt = now,
            });
        }
        else
        {
            // Versions catalogued before this table existed were backfilled with the catalogue time.
            version.CreatedOn = source.CreatedOn;
        }

        foreach (var field in catalogue.Fields)
        {
            var status = policy.StatusOf(field);
            if (existing.TryGetValue((field.ParentFieldId, field.FieldId), out var entry))
            {
                entry.ExportStatus = status;
                entry.SemanticKey = Truncate(field.SemanticKey, 200);
                continue;
            }

            db.FieldCatalog.Add(ToEntry(source, field, status, now));
        }

        await UpsertRetirementsAsync(source, catalogue, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertRetirementsAsync(CatalogueSource source, TemplateCatalogue catalogue, DateTime now, CancellationToken cancellationToken)
    {
        var existing = (await db.TemplateFieldRetirements
                .Where(r => r.TenantId == source.TenantId && r.TemplateVersionId == source.TemplateVersionId)
                .ToListAsync(cancellationToken))
            .ToDictionary(r => (r.ParentFieldId, r.FieldId), KeyComparer.Instance);

        foreach (var retired in catalogue.RetiredFields)
        {
            var replacedBy = retired.ReplacedBy.Count == 0 ? null : Truncate(string.Join(", ", retired.ReplacedBy), 2000);
            if (existing.TryGetValue((retired.ParentFieldId, retired.FieldId), out var entry))
            {
                entry.ReplacedBy = replacedBy;
                continue;
            }

            db.TemplateFieldRetirements.Add(new TemplateFieldRetirement
            {
                TenantId = source.TenantId,
                TemplateVersionId = source.TemplateVersionId,
                ParentFieldId = Truncate(retired.ParentFieldId, 200)!,
                FieldId = Truncate(retired.FieldId, 200)!,
                TemplateId = source.TemplateId,
                ReplacedBy = replacedBy,
                CreatedAt = now,
            });
        }
    }

    private static FieldCatalogEntry ToEntry(CatalogueSource source, CatalogField field, ExportStatus status, DateTime now) => new()
    {
        TenantId = source.TenantId,
        TemplateVersionId = source.TemplateVersionId,
        ParentFieldId = field.ParentFieldId,
        FieldId = field.FieldId,
        ContractVersion = PrismVersions.ContractVersion,
        TemplateId = source.TemplateId,
        TemplateVersionNumber = source.TemplateVersionNumber,
        FlowId = field.FlowId,
        FlowMode = field.FlowMode?.ToString(),
        TaskGroupId = field.TaskGroupId,
        TaskGroupName = field.TaskGroupName,
        TaskId = field.TaskId,
        TaskName = field.TaskName,
        PageId = field.PageId,
        PageTitle = field.PageTitle,
        FieldOrder = field.FieldOrder,
        Label = Truncate(field.Label, 1000),
        DataType = field.DataType,
        ControlType = field.ControlType,
        IsCollection = field.IsCollection,
        IsRequired = field.IsRequired,
        ChoicesJson = field.Options.Count == 0
            ? null
            : JsonSerializer.Serialize(field.Options.Select(o => new { value = o.Value, label = o.Label })),
        SemanticKey = Truncate(field.SemanticKey, 200),
        ExportStatus = status,
        CreatedAt = now,
    };

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: UniqueKeyViolation or UniqueIndexViolation };

    private sealed class KeyComparer : IEqualityComparer<(string Parent, string Field)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Parent, string Field) x, (string Parent, string Field) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Parent, y.Parent)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Field, y.Field);

        public int GetHashCode((string Parent, string Field) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Parent),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Field));
    }
}
