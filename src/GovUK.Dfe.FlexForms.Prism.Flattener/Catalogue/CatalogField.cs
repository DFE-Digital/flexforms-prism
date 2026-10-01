namespace GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

/// <summary>
/// Metadata for one field of a template version. Collection fields have <see cref="IsCollection"/> set and
/// their nested fields carry the collection's field id in <see cref="ParentFieldId"/>.
/// </summary>
public sealed record CatalogField
{
    public required string FieldId { get; init; }

    /// <summary>The collection field that contains this field, or empty for top-level fields.</summary>
    public string ParentFieldId { get; init; } = string.Empty;

    public string? FlowId { get; init; }
    public FlowMode? FlowMode { get; init; }
    public string? TaskGroupId { get; init; }
    public string? TaskGroupName { get; init; }
    public string? TaskId { get; init; }
    public string? TaskName { get; init; }
    public string? PageId { get; init; }
    public string? PageTitle { get; init; }

    /// <summary>Position of the field across the whole template, in display order.</summary>
    public int FieldOrder { get; init; }

    public string? Label { get; init; }

    /// <summary>The response data type the front end assigns to this field, if its type is known.</summary>
    public string? DataType { get; init; }

    /// <summary>The template field type, such as <c>radios</c> or <c>complexField</c>.</summary>
    public string? ControlType { get; init; }

    public string? ComplexFieldId { get; init; }
    public bool IsCollection { get; init; }
    public bool? IsRequired { get; init; }
    public IReadOnlyList<CatalogOption> Options { get; init; } = [];
}

public sealed record CatalogOption(string Value, string Label);

public enum FlowMode
{
    MultiCollection,
    DerivedCollection,
}
