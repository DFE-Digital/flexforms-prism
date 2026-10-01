using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Text;

/// <summary>
/// Writes JSON with object properties sorted ordinally and no whitespace, so equal values always produce
/// identical text regardless of the property order they were stored in.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <param name="element">The value to write.</param>
    /// <param name="decodeStrings">Decode stored HTML encoding in every string value.</param>
    public static string Write(JsonElement element, bool decodeStrings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteElement(writer, element, decodeStrings);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string WriteStrings(IEnumerable<string> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, bool decodeStrings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, decodeStrings);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, decodeStrings);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                writer.WriteStringValue(decodeStrings ? StoredText.Decode(text) : text);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
