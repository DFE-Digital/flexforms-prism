namespace GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

/// <summary>
/// One flattened answer: a scalar field, a field on a collection item, or a promoted nested property.
/// </summary>
/// <param name="LogicalKeyHash">SHA-256 of the canonical logical key; unique within a generation.</param>
/// <param name="FieldId">Template field identifier.</param>
/// <param name="ParentFieldId">Collection field that contains this answer, or empty for top-level answers.</param>
/// <param name="OccurrencePath">Canonical ancestry of collection items, or empty for top-level answers.</param>
/// <param name="ItemId">Identifier of the innermost collection item, if any.</param>
/// <param name="ItemOrdinal">Display position of the innermost collection item; not part of identity.</param>
/// <param name="NestedPath">Path of a promoted property inside a complex value, or empty.</param>
/// <param name="DataType">Data type declared by the response envelope or template.</param>
/// <param name="IsCompleted">Completion flag from the response envelope.</param>
/// <param name="InterpretationStatus">How the raw value was interpreted.</param>
/// <param name="ValueString">String value.</param>
/// <param name="ValueDecimal">Numeric value.</param>
/// <param name="ValueBool">Boolean value.</param>
/// <param name="ValueDate">Date-only value.</param>
/// <param name="ValueDateTime">Date and time value.</param>
/// <param name="ValueJson">JSON for arrays, objects and unsupported shapes.</param>
/// <param name="RawValue">The raw value as received.</param>
public sealed record AnswerFact(
    byte[] LogicalKeyHash,
    string FieldId,
    string ParentFieldId,
    string OccurrencePath,
    string? ItemId,
    int? ItemOrdinal,
    string NestedPath,
    string? DataType,
    bool? IsCompleted,
    InterpretationStatus InterpretationStatus,
    string? ValueString,
    decimal? ValueDecimal,
    bool? ValueBool,
    DateOnly? ValueDate,
    DateTime? ValueDateTime,
    string? ValueJson,
    string? RawValue);
