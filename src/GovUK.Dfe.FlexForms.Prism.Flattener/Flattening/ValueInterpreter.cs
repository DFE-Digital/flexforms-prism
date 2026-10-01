using System.Globalization;
using System.Text;
using System.Text.Json;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Flattening;

/// <summary>The value part of a fact, before it is placed in a collection item and keyed.</summary>
internal sealed record ValueFact(
    string NestedPath,
    InterpretationStatus Status,
    string? DataType = null,
    string? ValueString = null,
    decimal? ValueDecimal = null,
    bool? ValueBool = null,
    DateOnly? ValueDate = null,
    DateTime? ValueDateTime = null,
    string? ValueJson = null,
    string? RawValue = null);

/// <summary>
/// Interprets one stored answer into typed values, following how the web front end stores each field type:
/// <list type="bullet">
/// <item>scalars are strings, HTML-encoded by the input sanitiser;</item>
/// <item>dates are <c>yyyy-MM-dd</c>, and invalid input is stored as typed;</item>
/// <item>checkboxes are one plain code, or a JSON array of codes when several are selected;</item>
/// <item>complex fields hold autocomplete objects (or arrays of them) or upload metadata arrays as JSON.</item>
/// </list>
/// </summary>
internal static class ValueInterpreter
{
    public const int MaxNestedPathLength = 300;
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly decimal DecimalLimit = 1e28m;
    private static readonly string[] UploadSummaryProperties =
        ["fileSize", "id", "name", "originalFileName", "uploadedOn", "validationStatus"];

    private enum ControlKind
    {
        Scalar,
        Checkboxes,
        Complex,
        Date,
    }

    /// <param name="field">The catalogued field.</param>
    /// <param name="value">The stored value.</param>
    /// <param name="dataType">The effective data type for scalar interpretation.</param>
    /// <param name="encoded">Whether strings in the value may still carry the sanitiser's HTML encoding.</param>
    public static IReadOnlyList<ValueFact> Interpret(CatalogField field, JsonElement value, string? dataType, bool encoded)
    {
        var kind = Classify(field.ControlType);
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return [new ValueFact(string.Empty, InterpretationStatus.Null)];
            case JsonValueKind.Array:
            case JsonValueKind.Object:
                return InterpretStructured(kind, value, encoded, raw: value.GetRawText());
            case JsonValueKind.String:
                return InterpretString(kind, value.GetString()!, dataType, encoded);
            default:
                return [InterpretScalar(kind, value.GetRawText(), value.GetRawText(), dataType)];
        }
    }

    private static IReadOnlyList<ValueFact> InterpretString(ControlKind kind, string stored, string? dataType, bool encoded)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [new ValueFact(string.Empty, InterpretationStatus.Empty, RawValue: stored)];
        }

        if (kind is ControlKind.Checkboxes or ControlKind.Complex
            && TryParseStoredJson(stored, out var element, out var leavesEncoded))
        {
            return InterpretStructured(kind, element, encoded && leavesEncoded, stored);
        }

        var text = Decode(stored, encoded);
        return kind == ControlKind.Checkboxes
            ? Codes([text], stored)
            : [InterpretScalar(kind, text, stored, dataType)];
    }

    private static ValueFact InterpretScalar(ControlKind kind, string text, string raw, string? dataType)
    {
        var trimmed = text.Trim();
        if (kind == ControlKind.Date)
        {
            return DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? new ValueFact(string.Empty, InterpretationStatus.Ok, ValueDate: date, RawValue: raw)
                : new ValueFact(string.Empty, InterpretationStatus.ParseFailed, RawValue: raw);
        }

        switch (dataType)
        {
            case FieldDataTypes.DateTime:
                if (DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
                {
                    return new ValueFact(string.Empty, InterpretationStatus.Ok, ValueDate: dateOnly, RawValue: raw);
                }

                return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime)
                    ? new ValueFact(string.Empty, InterpretationStatus.Ok, ValueDateTime: dateTime.UtcDateTime, RawValue: raw)
                    : new ValueFact(string.Empty, InterpretationStatus.ParseFailed, RawValue: raw);
            case FieldDataTypes.Number:
                return TryParseDecimal(trimmed, out var number)
                    ? new ValueFact(string.Empty, InterpretationStatus.Ok, ValueDecimal: number, RawValue: raw)
                    : new ValueFact(string.Empty, InterpretationStatus.ParseFailed, RawValue: raw);
            case FieldDataTypes.Boolean:
                return bool.TryParse(trimmed, out var flag)
                    ? new ValueFact(string.Empty, InterpretationStatus.Ok, ValueBool: flag, RawValue: raw)
                    : new ValueFact(string.Empty, InterpretationStatus.ParseFailed, RawValue: raw);
            default:
                return new ValueFact(string.Empty, InterpretationStatus.Ok, ValueString: text, RawValue: raw);
        }
    }

    private static IReadOnlyList<ValueFact> InterpretStructured(ControlKind kind, JsonElement element, bool encoded, string raw)
    {
        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0)
        {
            return [new ValueFact(string.Empty, InterpretationStatus.Empty, RawValue: raw)];
        }

        if (kind == ControlKind.Checkboxes && IsArrayOf(element, JsonValueKind.String))
        {
            var codes = element.EnumerateArray()
                .Select(e => e.GetString()!)
                .Select(code => encoded ? StoredText.Decode(code) : code)
                .ToList();
            return Codes(codes, raw);
        }

        if (kind == ControlKind.Complex)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                return ComplexObject(string.Empty, element, encoded, raw);
            }

            if (IsArrayOf(element, JsonValueKind.Object))
            {
                return element.EnumerateArray().All(IsUpload)
                    ? Uploads(element, encoded)
                    : element.EnumerateArray()
                        .SelectMany((item, i) => ComplexObject(PathSegment.Synthetic(i), item, encoded, raw: null))
                        .ToList();
            }
        }

        return [new ValueFact(string.Empty, InterpretationStatus.Unsupported, ValueJson: CanonicalJson.Write(element, encoded), RawValue: raw)];
    }

    /// <summary>Selected options: one fact for the selection as a whole, plus one per selected code.</summary>
    private static List<ValueFact> Codes(IReadOnlyList<string> codes, string raw)
    {
        var facts = new List<ValueFact>(codes.Count + 1)
        {
            new(string.Empty, InterpretationStatus.Ok, ValueJson: CanonicalJson.WriteStrings(codes), RawValue: raw),
        };

        foreach (var code in codes)
        {
            facts.Add(new ValueFact(
                PathSegment.Fit(PathSegment.Escape(code), MaxNestedPathLength),
                InterpretationStatus.Ok,
                DataType: FieldDataTypes.Boolean,
                ValueString: code,
                ValueBool: true));
        }

        return facts;
    }

    /// <summary>An autocomplete selection: the whole object, plus each scalar property promoted to its own fact.</summary>
    private static List<ValueFact> ComplexObject(string prefix, JsonElement element, bool encoded, string? raw)
    {
        var name = element.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
            ? Decode(nameProperty.GetString()!, encoded)
            : null;

        var facts = new List<ValueFact>
        {
            new(prefix, InterpretationStatus.Ok, ValueString: name, ValueJson: CanonicalJson.Write(element, encoded), RawValue: raw),
        };

        foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var path = PathSegment.Fit(Join(prefix, PathSegment.Escape(property.Name)), MaxNestedPathLength);
            var promoted = property.Value.ValueKind switch
            {
                JsonValueKind.String => new ValueFact(path, InterpretationStatus.Ok, FieldDataTypes.String, ValueString: Decode(property.Value.GetString()!, encoded)),
                JsonValueKind.Number => TryParseDecimal(property.Value.GetRawText(), out var number)
                    ? new ValueFact(path, InterpretationStatus.Ok, FieldDataTypes.Number, ValueDecimal: number)
                    : new ValueFact(path, InterpretationStatus.ParseFailed, FieldDataTypes.Number, RawValue: property.Value.GetRawText()),
                JsonValueKind.True or JsonValueKind.False => new ValueFact(path, InterpretationStatus.Ok, FieldDataTypes.Boolean, ValueBool: property.Value.GetBoolean()),
                JsonValueKind.Null => new ValueFact(path, InterpretationStatus.Null),
                _ => null,
            };

            if (promoted is not null)
            {
                facts.Add(promoted);
            }
        }

        return facts;
    }

    /// <summary>
    /// Uploaded files: one fact per file with a metadata summary only. The raw value is not kept because it
    /// carries uploader identifiers and storage file names.
    /// </summary>
    private static List<ValueFact> Uploads(JsonElement files, bool encoded)
    {
        var facts = new List<ValueFact>();
        var index = 0;
        foreach (var file in files.EnumerateArray())
        {
            var id = file.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : null;
            var path = string.IsNullOrWhiteSpace(id) ? PathSegment.Synthetic(index) : PathSegment.Escape(id);
            var fileName = StringProperty(file, "originalFileName") ?? StringProperty(file, "name");

            facts.Add(new ValueFact(
                PathSegment.Fit(path, MaxNestedPathLength),
                InterpretationStatus.Ok,
                ValueString: fileName is null ? null : Decode(fileName, encoded),
                ValueJson: UploadSummary(file, encoded)));
            index++;
        }

        return facts;
    }

    private static string UploadSummary(JsonElement file, bool encoded)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var name in UploadSummaryProperties)
            {
                if (file.TryGetProperty(name, out var property))
                {
                    writer.WritePropertyName(name);
                    writer.WriteRawValue(CanonicalJson.Write(property, encoded));
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parses JSON held in a stored string. JSON built by the server is stored as is and only its string values
    /// may be encoded; JSON posted from a form field was HTML-encoded as a whole.
    /// </summary>
    internal static bool TryParseStoredJson(string stored, out JsonElement element, out bool encoded)
    {
        var trimmed = stored.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            element = default;
            encoded = false;
            return false;
        }

        if (TryParseJson(stored, out element))
        {
            encoded = true;
            return true;
        }

        encoded = false;
        return TryParseJson(StoredText.Decode(stored), out element);
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    internal static bool TryParseDecimal(string text, out decimal value)
    {
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && Math.Abs(parsed) < DecimalLimit)
        {
            value = Math.Round(parsed, 10, MidpointRounding.ToEven);
            return true;
        }

        value = 0;
        return false;
    }

    private static ControlKind Classify(string? controlType) =>
        controlType?.Trim().ToLowerInvariant() switch
        {
            "checkboxes" => ControlKind.Checkboxes,
            "complexfield" or "complex-field" or "autocomplete" => ControlKind.Complex,
            "date" => ControlKind.Date,
            _ => ControlKind.Scalar,
        };

    private static bool IsUpload(JsonElement item) =>
        item.TryGetProperty("originalFileName", out _) || item.TryGetProperty("fileName", out _);

    private static bool IsArrayOf(JsonElement element, JsonValueKind kind) =>
        element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(e => e.ValueKind == kind);

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Decode(string text, bool encoded) => encoded ? StoredText.Decode(text) : text;

    private static string Join(string prefix, string segment) =>
        prefix.Length == 0 ? segment : prefix + PathSegment.Separator + segment;
}
