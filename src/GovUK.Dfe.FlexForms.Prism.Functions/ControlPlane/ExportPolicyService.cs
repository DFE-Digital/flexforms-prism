using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

/// <summary><see cref="Decision"/> is nullable so a missing value is rejected rather than read as Allowed.</summary>
public sealed record ExportDecisionRequest(string? ParentFieldId, string FieldId, ExportDecision? Decision, string? Reason);

public sealed record FieldClassification(
    string ParentFieldId,
    string FieldId,
    string? Label,
    string? DataType,
    bool IsCollection,
    string? TaskName,
    string? PageTitle,
    string? TemplateVersionNumber,
    ExportStatus ExportStatus,
    string? Reason,
    string? DecidedBy,
    DateTime? DecidedAt,
    int? PolicyVersion);

public sealed record ExportPolicyView(
    Guid TenantId,
    Guid TemplateId,
    int PolicyVersion,
    DefaultExportMode DefaultMode,
    ExportDefaultSource DefaultSource,
    IReadOnlyList<FieldClassification> Fields);

public enum ApplyStatus
{
    Applied,
    Unchanged,
    UnknownFields,
    Invalid
}

public sealed record ApplyResult(ApplyStatus Status, int PolicyVersion, int Changed, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

/// <summary>A default as set by an admin: <see cref="Inherit"/> clears it so the next level applies.</summary>
public enum ExportDefaultChoice
{
    Inherit,
    ApproveFirst,
    ExportAll
}

/// <summary><see cref="Mode"/> is nullable so a missing value is rejected rather than read as Inherit.</summary>
public sealed record ExportDefaultRequest(ExportDefaultChoice? Mode, string? Reason);

/// <summary>
/// A tenant or template default. <see cref="Mode"/> is what was set at this level; <see cref="EffectiveMode"/> and
/// <see cref="Source"/> are what applies once inheritance is resolved.
/// </summary>
public sealed record ExportDefaultView(
    Guid TenantId,
    Guid? TemplateId,
    ExportDefaultChoice Mode,
    DefaultExportMode EffectiveMode,
    ExportDefaultSource Source,
    string? Reason,
    string? DecidedBy,
    DateTime? DecidedAt,
    int? PolicyVersion);

public sealed record DefaultChangeResult(ApplyStatus Status, int PolicyVersion, ExportDefaultView? Default, IReadOnlyList<string> Problems);

/// <summary>
/// Reads and changes a template's export policy: explicit field decisions plus the default for undecided fields.
/// Every change raises the policy version of each template it affects, which makes the projector re-project the
/// affected applications at their current revision.
/// </summary>
public sealed class ExportPolicyService(PrismDbContext db, TimeProvider clock)
{
    /// <summary>Every catalogued field of the template with its status, or null if nothing is known about the template.</summary>
    public async Task<ExportPolicyView?> GetAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken)
    {
        var catalogued = await LatestCatalogueAsync(tenantId, templateId, cancellationToken);
        var decisions = await db.FieldExportPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.TemplateId == templateId)
            .ToDictionaryAsync(p => (p.ParentFieldId, p.FieldId), cancellationToken);

        if (catalogued.Count == 0 && decisions.Count == 0)
        {
            return null;
        }

        var resolved = await ExportPolicyReader.GetDefaultAsync(db, tenantId, templateId, cancellationToken);
        var policy = new ExportPolicy(0, decisions.Values.Select(d => new ExportRule(d.ParentFieldId, d.FieldId, d.Decision)), resolved.Mode);
        var fields = catalogued
            .Select(c => Classify(c.ParentFieldId, c.FieldId, c, decisions.GetValueOrDefault((c.ParentFieldId, c.FieldId)), policy))
            .Concat(decisions.Values
                .Where(d => !catalogued.Any(c => c.ParentFieldId == d.ParentFieldId && c.FieldId == d.FieldId))
                .Select(d => Classify(d.ParentFieldId, d.FieldId, null, d, policy)))
            .ToList();

        var version = Math.Max(resolved.Version, decisions.Count == 0 ? 0 : decisions.Values.Max(d => d.PolicyVersion));
        return new ExportPolicyView(tenantId, templateId, version, resolved.Mode, resolved.Source, fields);
    }

    public async Task<ApplyResult> ApplyAsync(
        Guid tenantId,
        Guid templateId,
        IReadOnlyList<ExportDecisionRequest> requested,
        string decidedBy,
        CancellationToken cancellationToken)
    {
        var invalid = Validate(requested);
        if (invalid.Count > 0)
        {
            return new ApplyResult(ApplyStatus.Invalid, 0, 0, invalid, []);
        }

        var catalogued = (await LatestCatalogueAsync(tenantId, templateId, cancellationToken))
            .Select(c => (c.ParentFieldId, c.FieldId))
            .ToHashSet();
        var unknown = requested
            .Where(r => !catalogued.Contains((r.ParentFieldId ?? string.Empty, r.FieldId)))
            .Select(r => Key(r.ParentFieldId ?? string.Empty, r.FieldId))
            .ToList();
        if (unknown.Count > 0)
        {
            return new ApplyResult(ApplyStatus.UnknownFields, 0, 0, unknown, []);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var currentVersion = await LockVersionAsync(tenantId, templateId, cancellationToken);
        var defaultMode = (await ExportPolicyReader.GetDefaultAsync(db, tenantId, templateId, cancellationToken)).Mode;

        var existing = await db.FieldExportPolicies
            .Where(p => p.TenantId == tenantId && p.TemplateId == templateId)
            .ToDictionaryAsync(p => (p.ParentFieldId, p.FieldId), cancellationToken);

        var nextVersion = currentVersion + 1;
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = 0;
        foreach (var request in requested)
        {
            var key = (request.ParentFieldId ?? string.Empty, request.FieldId);
            if (existing.TryGetValue(key, out var row))
            {
                if (row.Decision == request.Decision && row.Reason == request.Reason)
                {
                    continue;
                }
            }
            else
            {
                row = new FieldExportPolicy { TenantId = tenantId, TemplateId = templateId, ParentFieldId = key.Item1, FieldId = key.FieldId };
                db.FieldExportPolicies.Add(row);
                existing[key] = row;
            }

            row.Decision = request.Decision!.Value;
            row.Reason = request.Reason;
            row.PolicyVersion = nextVersion;
            row.DecidedBy = decidedBy;
            row.DecidedAt = now;
            changed++;
        }

        if (changed == 0)
        {
            return new ApplyResult(ApplyStatus.Unchanged, currentVersion, 0, [], Warnings(existing.Values, defaultMode));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ApplyResult(ApplyStatus.Applied, nextVersion, changed, [], Warnings(existing.Values, defaultMode));
    }

    /// <summary>The tenant-wide default when <paramref name="templateId"/> is null, otherwise the template's.</summary>
    public async Task<ExportDefaultView> GetDefaultAsync(Guid tenantId, Guid? templateId, CancellationToken cancellationToken)
    {
        var rows = await db.ExportDefaults.AsNoTracking()
            .Where(d => d.TenantId == tenantId && (d.TemplateId == Guid.Empty || d.TemplateId == templateId))
            .ToListAsync(cancellationToken);
        return View(tenantId, templateId, rows);
    }

    /// <summary>
    /// Sets the tenant-wide default when <paramref name="templateId"/> is null, otherwise the template's. A tenant
    /// change takes a version above every template's, so all of them re-project.
    /// </summary>
    public async Task<DefaultChangeResult> SetDefaultAsync(
        Guid tenantId,
        Guid? templateId,
        ExportDefaultRequest request,
        string decidedBy,
        CancellationToken cancellationToken)
    {
        if (request.Mode is not { } choice || !Enum.IsDefined(choice))
        {
            return new DefaultChangeResult(ApplyStatus.Invalid, 0, null, ["Mode must be Inherit, ApproveFirst or ExportAll."]);
        }

        if (templateId == Guid.Empty)
        {
            return new DefaultChangeResult(ApplyStatus.Invalid, 0, null, ["The empty template id is reserved for the tenant default."]);
        }

        var rowTemplateId = templateId ?? Guid.Empty;
        var mode = choice switch
        {
            ExportDefaultChoice.ApproveFirst => DefaultExportMode.ApproveFirst,
            ExportDefaultChoice.ExportAll => DefaultExportMode.ExportAll,
            _ => (DefaultExportMode?)null,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var currentVersion = await LockVersionAsync(tenantId, templateId, cancellationToken);

        var rows = await db.ExportDefaults
            .Where(d => d.TenantId == tenantId && (d.TemplateId == Guid.Empty || d.TemplateId == rowTemplateId))
            .ToListAsync(cancellationToken);
        var row = rows.SingleOrDefault(r => r.TemplateId == rowTemplateId);

        if (row is null ? mode is null : row.Mode == mode && row.Reason == request.Reason)
        {
            return new DefaultChangeResult(ApplyStatus.Unchanged, currentVersion, View(tenantId, templateId, rows), []);
        }

        if (row is null)
        {
            row = new ExportDefault { TenantId = tenantId, TemplateId = rowTemplateId };
            db.ExportDefaults.Add(row);
            rows.Add(row);
        }

        var nextVersion = currentVersion + 1;
        row.Mode = mode;
        row.Reason = request.Reason;
        row.PolicyVersion = nextVersion;
        row.DecidedBy = decidedBy;
        row.DecidedAt = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DefaultChangeResult(ApplyStatus.Applied, nextVersion, View(tenantId, templateId, rows), []);
    }

    /// <summary>
    /// The highest policy version in scope: one template's decisions and the defaults that apply to it, or with a
    /// null <paramref name="templateId"/> every policy row of the tenant. The rows are range-locked so concurrent
    /// changes get distinct, increasing versions.
    /// </summary>
    private async Task<int> LockVersionAsync(Guid tenantId, Guid? templateId, CancellationToken cancellationToken)
    {
        var query = templateId is { } id
            ? db.Database.SqlQuery<int?>($"""
                SELECT MAX(v) AS Value FROM (
                    SELECT policy_version AS v FROM prism.field_export_policy WITH (UPDLOCK, HOLDLOCK)
                    WHERE tenant_id = {tenantId} AND template_id = {id}
                    UNION ALL
                    SELECT policy_version FROM prism.export_defaults WITH (UPDLOCK, HOLDLOCK)
                    WHERE tenant_id = {tenantId} AND template_id IN ({id}, {Guid.Empty})
                ) AS versions
                """)
            : db.Database.SqlQuery<int?>($"""
                SELECT MAX(v) AS Value FROM (
                    SELECT policy_version AS v FROM prism.field_export_policy WITH (UPDLOCK, HOLDLOCK)
                    WHERE tenant_id = {tenantId}
                    UNION ALL
                    SELECT policy_version FROM prism.export_defaults WITH (UPDLOCK, HOLDLOCK)
                    WHERE tenant_id = {tenantId}
                ) AS versions
                """);
        return await query.SingleAsync(cancellationToken) ?? 0;
    }

    private static ExportDefaultView View(Guid tenantId, Guid? templateId, IReadOnlyCollection<ExportDefault> rows)
    {
        var tenantRow = rows.SingleOrDefault(r => r.TemplateId == Guid.Empty);
        var templateRow = templateId is null ? null : rows.SingleOrDefault(r => r.TemplateId == templateId);
        var own = templateId is null ? tenantRow : templateRow;
        var resolved = ExportPolicyReader.Resolve(tenantRow, templateRow);
        return new ExportDefaultView(
            tenantId,
            templateId,
            own?.Mode switch
            {
                DefaultExportMode.ApproveFirst => ExportDefaultChoice.ApproveFirst,
                DefaultExportMode.ExportAll => ExportDefaultChoice.ExportAll,
                _ => ExportDefaultChoice.Inherit,
            },
            resolved.Mode,
            resolved.Source,
            own?.Reason,
            own?.DecidedBy,
            own?.DecidedAt,
            own?.PolicyVersion);
    }

    private static List<string> Validate(IReadOnlyList<ExportDecisionRequest> requested)
    {
        var problems = new List<string>();
        if (requested.Count == 0)
        {
            problems.Add("At least one decision is required.");
        }

        if (requested.Any(r => string.IsNullOrWhiteSpace(r.FieldId)))
        {
            problems.Add("Every decision needs a fieldId.");
        }

        if (requested.Any(r => r.Decision is not (ExportDecision.Allowed or ExportDecision.Denied)))
        {
            problems.Add("Decision must be Allowed or Denied.");
        }

        problems.AddRange(requested
            .GroupBy(r => Key(r.ParentFieldId ?? string.Empty, r.FieldId))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} appears more than once."));
        return problems;
    }

    /// <summary>A nested field is only exported when its collection is exported too.</summary>
    private static List<string> Warnings(IEnumerable<FieldExportPolicy> decisions, DefaultExportMode defaultMode)
    {
        var rows = decisions.ToList();
        var policy = new ExportPolicy(0, rows.Select(r => new ExportRule(r.ParentFieldId, r.FieldId, r.Decision)), defaultMode);
        return rows
            .Where(r => r.ParentFieldId.Length > 0 && r.Decision == ExportDecision.Allowed
                && policy.StatusOf(string.Empty, r.ParentFieldId) is not (ExportStatus.Allowed or ExportStatus.AllowedByDefault))
            .Select(r => $"{Key(r.ParentFieldId, r.FieldId)} is allowed but its collection {r.ParentFieldId} is not, so it will not be exported.")
            .ToList();
    }

    /// <summary>One entry per field, taken from the most recently catalogued template version that has it.</summary>
    private async Task<List<FieldCatalogEntry>> LatestCatalogueAsync(Guid tenantId, Guid templateId, CancellationToken cancellationToken)
    {
        var entries = await db.FieldCatalog.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.TemplateId == templateId && c.ContractVersion == PrismVersions.ContractVersion)
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(c => (c.ParentFieldId, c.FieldId))
            .Select(g => g.OrderByDescending(c => c.CreatedAt).First())
            .OrderBy(c => c.TaskName).ThenBy(c => c.PageTitle).ThenBy(c => c.ParentFieldId).ThenBy(c => c.FieldOrder)
            .ToList();
    }

    private static FieldClassification Classify(
        string parentFieldId, string fieldId, FieldCatalogEntry? catalogued, FieldExportPolicy? decision, ExportPolicy policy) => new(
        parentFieldId,
        fieldId,
        catalogued?.Label,
        catalogued?.DataType,
        catalogued?.IsCollection ?? false,
        catalogued?.TaskName,
        catalogued?.PageTitle,
        catalogued?.TemplateVersionNumber,
        policy.StatusOf(parentFieldId, fieldId),
        decision?.Reason,
        decision?.DecidedBy,
        decision?.DecidedAt,
        decision?.PolicyVersion);

    private static string Key(string parentFieldId, string fieldId) => parentFieldId.Length == 0 ? fieldId : $"{parentFieldId}.{fieldId}";
}
