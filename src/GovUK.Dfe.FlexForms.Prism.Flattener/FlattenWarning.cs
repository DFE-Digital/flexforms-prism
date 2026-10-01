namespace GovUK.Dfe.FlexForms.Prism.Flattener;

/// <summary>Something in a response that was skipped or could not be fully flattened.</summary>
/// <param name="Code">One of <see cref="FlattenWarningCodes"/>.</param>
/// <param name="Key">The response key or path the warning is about.</param>
public sealed record FlattenWarning(string Code, string Key);

public static class FlattenWarningCodes
{
    /// <summary>A response key that matches nothing in the template.</summary>
    public const string UnknownKey = "UnknownKey";

    /// <summary>A form-plumbing key such as the anti-forgery token or a task completion checkbox.</summary>
    public const string NoiseKey = "NoiseKey";

    /// <summary>A collection's nested field stored at the top level. The per-item copy is authoritative.</summary>
    public const string NestedFieldAtTopLevel = "NestedFieldAtTopLevel";

    public const string UnknownNestedField = "UnknownNestedField";
    public const string MalformedCollection = "MalformedCollection";
    public const string MalformedCollectionItem = "MalformedCollectionItem";
    public const string PathTooLong = "PathTooLong";
    public const string DuplicateResponseKey = "DuplicateResponseKey";
    public const string DuplicateLogicalKey = "DuplicateLogicalKey";
}
