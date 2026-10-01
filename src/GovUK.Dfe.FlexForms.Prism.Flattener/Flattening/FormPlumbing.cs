namespace GovUK.Dfe.FlexForms.Prism.Flattener.Flattening;

/// <summary>
/// Keys the web front end posts alongside answers and that end up stored with them: page-handler fields,
/// the anti-forgery token, model-binding prefixes and autocomplete search inputs. Only consulted for keys
/// that match no catalogued field, so a template field with one of these names is still read.
/// </summary>
internal static class FormPlumbing
{
    private const string QuerySuffix = "_query";

    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "__RequestVerificationToken", "handler", "CurrentPageId", "TaskId", "FlowId", "InstanceId",
        "IsTaskCompleted", "_metadata",
    };

    private static readonly string[] Prefixes = ["Data[", "Data_", "CurrentTask."];

    /// <param name="key">The unmatched key.</param>
    /// <param name="isFieldInScope">Whether a field id exists where the key was found.</param>
    public static bool IsPlumbing(string key, Func<string, bool> isFieldInScope) =>
        Keys.Contains(key)
        || Prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || (key.Length > QuerySuffix.Length
            && key.EndsWith(QuerySuffix, StringComparison.OrdinalIgnoreCase)
            && isFieldInScope(key[..^QuerySuffix.Length]));
}
