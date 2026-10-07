namespace GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

/// <summary>
/// How a raw answer value was interpreted into the typed fact columns.
/// </summary>
public enum InterpretationStatus
{
    /// <summary>The value was interpreted into the typed column for its data type.</summary>
    Ok,

    /// <summary>The answer is present but null.</summary>
    Null,

    /// <summary>The answer is an empty string or an empty collection.</summary>
    Empty,

    /// <summary>The value could not be parsed as its declared data type; only the raw value is kept.</summary>
    ParseFailed,

    /// <summary>The shape is not supported by any extraction rule; the value is kept as JSON.</summary>
    Unsupported
}
