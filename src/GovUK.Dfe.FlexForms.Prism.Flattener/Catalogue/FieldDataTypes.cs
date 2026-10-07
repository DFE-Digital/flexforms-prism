namespace GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

/// <summary>
/// Data type names used in response envelopes. <see cref="FromTemplateType"/> mirrors
/// <c>ResponseFieldMetadataResolver.MapTemplateTypeToDataType</c> in the web front end.
/// </summary>
public static class FieldDataTypes
{
    public const string String = "string";
    public const string DateTime = "DateTime";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Array = "array";

    public static string? FromTemplateType(string? fieldType) =>
        fieldType?.Trim().ToLowerInvariant() switch
        {
            "date" or "datetime" or "date-time" => DateTime,
            "text" or "textarea" or "text-area" or "character-count"
                or "email" or "select" or "radios" or "checkboxes"
                or "autocomplete" or "complexfield" or "complex-field" => String,
            "number" or "numeric" or "integer" or "decimal" => Number,
            "boolean" or "bool" => Boolean,
            _ => null,
        };
}
