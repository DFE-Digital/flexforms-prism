using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Policy;

/// <summary>An explicit decision for one field, scoped by its parent collection (empty for top-level fields).</summary>
public sealed record ExportRule(string ParentFieldId, string FieldId, ExportDecision Decision);

/// <summary>
/// The export decisions for a template. It fails closed: a field without an explicit Allowed decision never
/// produces facts. A nested field also needs its collection to be allowed.
/// </summary>
public sealed class ExportPolicy
{
    private readonly Dictionary<(string Parent, string Field), ExportDecision> decisions;

    public ExportPolicy(int version, IEnumerable<ExportRule> rules)
    {
        Version = version;
        decisions = new Dictionary<(string, string), ExportDecision>(KeyComparer.Instance);
        foreach (var rule in rules)
        {
            decisions[(rule.ParentFieldId, rule.FieldId)] = rule.Decision;
        }
    }

    /// <summary>A policy with no decisions, so nothing is exported.</summary>
    public static ExportPolicy DenyAll { get; } = new(0, []);

    /// <summary>The policy version, which is part of the fact hash so a policy change re-projects.</summary>
    public int Version { get; }

    public ExportStatus StatusOf(string parentFieldId, string fieldId) =>
        decisions.TryGetValue((parentFieldId, fieldId), out var decision)
            ? decision == ExportDecision.Allowed ? ExportStatus.Allowed : ExportStatus.Denied
            : ExportStatus.Unclassified;

    public ExportStatus StatusOf(CatalogField field) => StatusOf(field.ParentFieldId, field.FieldId);

    public bool IsExported(CatalogField field) =>
        StatusOf(field) == ExportStatus.Allowed
        && (field.ParentFieldId.Length == 0 || StatusOf(string.Empty, field.ParentFieldId) == ExportStatus.Allowed);

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
