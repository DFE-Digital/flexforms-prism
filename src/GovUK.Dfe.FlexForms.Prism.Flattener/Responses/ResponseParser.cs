using System.Text.Json;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Responses;

/// <summary>
/// One answer key from a response body, with its envelope unwrapped.
/// </summary>
/// <param name="Key">The response key, usually a field id.</param>
/// <param name="Value">The stored value. Current responses always store a string; legacy responses may not.</param>
/// <param name="Completed">The envelope's completed flag, if there was an envelope.</param>
/// <param name="DataType">The envelope's data type, if recorded.</param>
public sealed record ResponseEntry(string Key, JsonElement Value, bool? Completed, string? DataType);

public sealed record ParsedResponse(IReadOnlyList<ResponseEntry> Entries, IReadOnlyList<FlattenWarning> Warnings);

/// <summary>
/// Parses a response body as written by the web front end's <c>TransformToResponseJson</c>: an object of
/// <c>{ question, value, completed, dataType, fields? }</c> envelopes keyed by field id, plus
/// <c>TaskStatus_{taskId}</c> entries, which are skipped. Older bodies hold bare values instead of envelopes,
/// and some are wrapped in a <c>formData</c> or <c>data</c> object.
/// </summary>
public static class ResponseParser
{
    public const string TaskStatusPrefix = "TaskStatus_";

    private static readonly string[] WrapperNames = ["formData", "FormData", "data", "Data"];

    private static readonly HashSet<string> EnvelopeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "question", "value", "completed", "dataType", "fields",
    };

    public static ParsedResponse Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ParsedResponse([], []);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true });
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ResponseFormatException("The response body is not valid JSON.", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ResponseFormatException($"The response body is a JSON {root.ValueKind}, not an object.");
        }

        root = Unwrap(root);

        var entries = new List<ResponseEntry>();
        var warnings = new List<FlattenWarning>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.StartsWith(TaskStatusPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(property.Name))
            {
                warnings.Add(new FlattenWarning(FlattenWarningCodes.DuplicateResponseKey, property.Name));
                continue;
            }

            entries.Add(ToEntry(property.Name, property.Value));
        }

        return new ParsedResponse(entries, warnings);
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        using var properties = root.EnumerateObject();
        if (!properties.MoveNext())
        {
            return root;
        }

        var only = properties.Current;
        return !properties.MoveNext()
               && WrapperNames.Contains(only.Name, StringComparer.Ordinal)
               && only.Value.ValueKind == JsonValueKind.Object
            ? only.Value
            : root;
    }

    private static ResponseEntry ToEntry(string key, JsonElement element)
    {
        if (!IsEnvelope(element))
        {
            return new ResponseEntry(key, element, null, null);
        }

        JsonElement value = default;
        bool? completed = null;
        string? dataType = null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "value", StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
            }
            else if (string.Equals(property.Name, "completed", StringComparison.OrdinalIgnoreCase))
            {
                completed = property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(property.Value.GetString(), out var parsed) => parsed,
                    _ => null,
                };
            }
            else if (string.Equals(property.Name, "dataType", StringComparison.OrdinalIgnoreCase)
                     && property.Value.ValueKind == JsonValueKind.String)
            {
                dataType = string.IsNullOrWhiteSpace(property.Value.GetString()) ? null : property.Value.GetString();
            }
        }

        return new ResponseEntry(key, value, completed, dataType);
    }

    private static bool IsEnvelope(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasValue = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!EnvelopeProperties.Contains(property.Name))
            {
                return false;
            }

            hasValue |= string.Equals(property.Name, "value", StringComparison.OrdinalIgnoreCase);
        }

        return hasValue;
    }
}
