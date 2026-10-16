namespace GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

/// <summary>
/// Every field of one template version, with lookups used to route response keys. Field ids are matched
/// case-insensitively, as the web front end does.
/// </summary>
public sealed class TemplateCatalogue
{
    private readonly Dictionary<string, CatalogField> topLevel;
    private readonly Dictionary<string, CatalogCollection> collections;
    private readonly HashSet<string> nestedFieldIds;
    private readonly HashSet<string> taskIds;

    internal TemplateCatalogue(
        string? templateId,
        IReadOnlyList<CatalogField> fields,
        IReadOnlyList<CatalogCollection> collectionList,
        IEnumerable<string> taskIdList,
        IReadOnlyList<RetiredCatalogField> retiredFields,
        IReadOnlyList<string> warnings)
    {
        TemplateId = templateId;
        Fields = fields;
        Collections = collectionList;
        RetiredFields = retiredFields;
        Warnings = warnings;

        topLevel = fields
            .Where(f => f.ParentFieldId.Length == 0)
            .ToDictionary(f => f.FieldId, StringComparer.OrdinalIgnoreCase);
        collections = collectionList.ToDictionary(c => c.Field.FieldId, StringComparer.OrdinalIgnoreCase);
        nestedFieldIds = new HashSet<string>(
            fields.Where(f => f.ParentFieldId.Length > 0).Select(f => f.FieldId),
            StringComparer.OrdinalIgnoreCase);
        taskIds = new HashSet<string>(taskIdList, StringComparer.OrdinalIgnoreCase);
    }

    public string? TemplateId { get; }

    /// <summary>All fields in display order, including collection fields and their nested fields.</summary>
    public IReadOnlyList<CatalogField> Fields { get; }

    public IReadOnlyList<CatalogCollection> Collections { get; }

    /// <summary>The fields the template author retired in this version, as declared under <c>retiredFields</c>.</summary>
    public IReadOnlyList<RetiredCatalogField> RetiredFields { get; }

    /// <summary>Problems found while building the catalogue, such as duplicate field ids that were ignored.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool TryGetTopLevel(string fieldId, out CatalogField field) =>
        topLevel.TryGetValue(fieldId, out field!);

    public bool TryGetCollection(string fieldId, out CatalogCollection collection) =>
        collections.TryGetValue(fieldId, out collection!);

    public bool IsNestedFieldId(string fieldId) => nestedFieldIds.Contains(fieldId);

    public bool IsTaskId(string taskId) => taskIds.Contains(taskId);
}

/// <summary>
/// A multi-collection or derived collection flow, keyed by the field id its answers are stored under.
/// </summary>
public sealed class CatalogCollection
{
    private readonly Dictionary<string, CatalogField> nested;

    internal CatalogCollection(CatalogField field, FlowMode mode, IReadOnlyList<CatalogField> nestedFields, string? statusFieldId)
    {
        Field = field;
        Mode = mode;
        NestedFields = nestedFields;
        StatusFieldId = statusFieldId;
        nested = nestedFields.ToDictionary(f => f.FieldId, StringComparer.OrdinalIgnoreCase);
    }

    public CatalogField Field { get; }
    public FlowMode Mode { get; }
    public IReadOnlyList<CatalogField> NestedFields { get; }

    /// <summary>For derived flows, the nested field that holds each item's signing status.</summary>
    public string? StatusFieldId { get; }

    public bool TryGetNested(string fieldId, out CatalogField field) => nested.TryGetValue(fieldId, out field!);
}
