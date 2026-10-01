using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
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

public sealed record ExportPolicyView(Guid TenantId, Guid TemplateId, int PolicyVersion, IReadOnlyList<FieldClassification> Fields);

public enum ApplyStatus
{
    Applied,
    Unchanged,
    UnknownFields,
    Invalid
}

public sealed record ApplyResult(ApplyStatus Status, int PolicyVersion, int Changed, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and changes a template's export policy. Every change raises the policy version, which makes the
/// projector re-project the affected applications at their current revision.
/// </summary>
public sealed class ExportPolicyService(PrismDbContext db, TimeProvider clock)
{
    /// <summary>Every catalogued field of the template with its decision, or null if nothing is known about the template.</summary>
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

        var fields = catalogued
            .Select(c => Classify(c.ParentFieldId, c.FieldId, c, decisions.GetValueOrDefault((c.ParentFieldId, c.FieldId))))
            .Concat(decisions.Values
                .Where(d => !catalogued.Any(c => c.ParentFieldId == d.ParentFieldId && c.FieldId == d.FieldId))
                .Select(d => Classify(d.ParentFieldId, d.FieldId, null, d)))
            .ToList();

        return new ExportPolicyView(tenantId, templateId, decisions.Count == 0 ? 0 : decisions.Values.Max(d => d.PolicyVersion), fields);
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

        // Range-locks the template's rows so concurrent changes get distinct, increasing versions.
        var currentVersion = await db.Database
            .SqlQuery<int?>($"""
                SELECT MAX(policy_version) AS Value
                FROM prism.field_export_policy WITH (UPDLOCK, HOLDLOCK)
                WHERE tenant_id = {tenantId} AND template_id = {templateId}
                """)
            .SingleAsync(cancellationToken) ?? 0;

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
            return new ApplyResult(ApplyStatus.Unchanged, currentVersion, 0, [], Warnings(existing.Values));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ApplyResult(ApplyStatus.Applied, nextVersion, changed, [], Warnings(existing.Values));
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

    /// <summary>A nested field is only exported when its collection is allowed too.</summary>
    private static List<string> Warnings(IEnumerable<FieldExportPolicy> policy)
    {
        var rows = policy.ToList();
        var allowedTopLevel = rows
            .Where(r => r.ParentFieldId.Length == 0 && r.Decision == ExportDecision.Allowed)
            .Select(r => r.FieldId)
            .ToHashSet();
        return rows
            .Where(r => r.ParentFieldId.Length > 0 && r.Decision == ExportDecision.Allowed && !allowedTopLevel.Contains(r.ParentFieldId))
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

    private static FieldClassification Classify(string parentFieldId, string fieldId, FieldCatalogEntry? catalogued, FieldExportPolicy? decision) => new(
        parentFieldId,
        fieldId,
        catalogued?.Label,
        catalogued?.DataType,
        catalogued?.IsCollection ?? false,
        catalogued?.TaskName,
        catalogued?.PageTitle,
        catalogued?.TemplateVersionNumber,
        decision?.Decision switch
        {
            ExportDecision.Allowed => ExportStatus.Allowed,
            ExportDecision.Denied => ExportStatus.Denied,
            _ => ExportStatus.Unclassified,
        },
        decision?.Reason,
        decision?.DecidedBy,
        decision?.DecidedAt,
        decision?.PolicyVersion);

    private static string Key(string parentFieldId, string fieldId) => parentFieldId.Length == 0 ? fieldId : $"{parentFieldId}.{fieldId}";
}
