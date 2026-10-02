using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Reading;

public enum ExportDefaultSource
{
    BuiltIn,
    Tenant,
    Template
}

/// <summary>
/// The default that applies to a template's undecided fields and where it came from. <see cref="Version"/> is the
/// highest policy version of the default rows that apply, so changing either one raises the template's policy version.
/// </summary>
public sealed record ResolvedExportDefault(DefaultExportMode Mode, ExportDefaultSource Source, int Version);

/// <summary>
/// Resolves a template's export policy: its field decisions plus the default from the template override, the tenant
/// default or <see cref="DefaultExportMode.ApproveFirst"/>, in that order.
/// </summary>
public static class ExportPolicyReader
{
    public static async Task<ResolvedExportDefault> GetDefaultAsync(
        PrismDbContext db, Guid tenantId, Guid templateId, CancellationToken cancellationToken)
    {
        var rows = await db.ExportDefaults.AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.TemplateId == templateId || d.TemplateId == Guid.Empty))
            .ToListAsync(cancellationToken);
        return Resolve(rows.SingleOrDefault(r => r.TemplateId == Guid.Empty), rows.SingleOrDefault(r => r.TemplateId != Guid.Empty));
    }

    public static async Task<ExportPolicy> GetPolicyAsync(
        PrismDbContext db, Guid tenantId, Guid templateId, CancellationToken cancellationToken)
    {
        var decisions = await db.FieldExportPolicies
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.TemplateId == templateId)
            .Select(p => new { p.ParentFieldId, p.FieldId, p.Decision, p.PolicyVersion })
            .ToListAsync(cancellationToken);
        var resolved = await GetDefaultAsync(db, tenantId, templateId, cancellationToken);

        return new ExportPolicy(
            Math.Max(resolved.Version, decisions.Count == 0 ? 0 : decisions.Max(d => d.PolicyVersion)),
            decisions.Select(d => new ExportRule(d.ParentFieldId, d.FieldId, d.Decision)),
            resolved.Mode);
    }

    public static ResolvedExportDefault Resolve(ExportDefault? tenantRow, ExportDefault? templateRow)
    {
        var version = Math.Max(tenantRow?.PolicyVersion ?? 0, templateRow?.PolicyVersion ?? 0);
        return templateRow?.Mode is { } templateMode ? new(templateMode, ExportDefaultSource.Template, version)
            : tenantRow?.Mode is { } tenantMode ? new(tenantMode, ExportDefaultSource.Tenant, version)
            : new(DefaultExportMode.ApproveFirst, ExportDefaultSource.BuiltIn, version);
    }
}
