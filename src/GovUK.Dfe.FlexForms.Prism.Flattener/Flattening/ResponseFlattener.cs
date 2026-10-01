using System.Text.Json;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Flattener.Responses;
using GovUK.Dfe.FlexForms.Prism.Flattener.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Flattening;

/// <param name="Facts">Facts for exported fields, in response order.</param>
/// <param name="Warnings">Keys and values that were skipped or only partly flattened.</param>
/// <param name="WithheldAnswerCount">Answers to catalogued fields that the export policy does not allow.</param>
public sealed record FlattenResult(IReadOnlyList<AnswerFact> Facts, IReadOnlyList<FlattenWarning> Warnings, int WithheldAnswerCount);

/// <summary>
/// Flattens a parsed response into answer facts using the catalogue of the template version it was saved
/// against. Response keys are routed as follows:
/// <list type="bullet">
/// <item>a top-level field becomes facts directly;</item>
/// <item>a multi-collection field holds a JSON array of items, each with an <c>id</c>;</item>
/// <item>a derived collection stores each item as <c>{fieldId}_status_{itemId}</c> and <c>{fieldId}_data_{itemId}</c>;</item>
/// <item>anything else is reported as a warning and skipped.</item>
/// </list>
/// Only fields the export policy allows produce facts.
/// </summary>
public static class ResponseFlattener
{
    private const string StatusInfix = "_status_";
    private const string DataInfix = "_data_";
    private const string CompletedSuffix = "_completed";
    private const int MaxItemIdLength = 200;

    public static FlattenResult Flatten(TemplateCatalogue catalogue, ParsedResponse response, ExportPolicy policy)
    {
        var run = new Run(catalogue, policy);
        run.Warnings.AddRange(response.Warnings);
        foreach (var entry in response.Entries)
        {
            run.Route(entry);
        }

        return new FlattenResult(run.Facts, run.Warnings, run.Withheld);
    }

    private sealed class Run(TemplateCatalogue catalogue, ExportPolicy policy)
    {
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);
        private readonly List<CatalogCollection> derived =
            catalogue.Collections.Where(c => c.Mode == FlowMode.DerivedCollection).ToList();

        public List<AnswerFact> Facts { get; } = [];
        public List<FlattenWarning> Warnings { get; } = [];
        public int Withheld { get; private set; }

        public void Route(ResponseEntry entry)
        {
            if (catalogue.TryGetCollection(entry.Key, out var collection))
            {
                if (collection.Mode == FlowMode.MultiCollection)
                {
                    AddMultiCollection(collection, entry);
                }
                else
                {
                    Warn(FlattenWarningCodes.UnknownKey, entry.Key);
                }

                return;
            }

            if (catalogue.TryGetTopLevel(entry.Key, out var field))
            {
                AddValue(field, entry.Value, entry.DataType, entry.Completed, string.Empty, null, null);
                return;
            }

            if (TryMatchDerived(entry.Key, out var derivedCollection, out var isStatus, out var itemId))
            {
                AddDerived(derivedCollection, isStatus, itemId, entry);
                return;
            }

            if (catalogue.IsNestedFieldId(entry.Key))
            {
                Warn(FlattenWarningCodes.NestedFieldAtTopLevel, entry.Key);
            }
            else if (IsNoise(entry.Key))
            {
                Warn(FlattenWarningCodes.NoiseKey, entry.Key);
            }
            else
            {
                Warn(FlattenWarningCodes.UnknownKey, entry.Key);
            }
        }

        private void AddMultiCollection(CatalogCollection collection, ResponseEntry entry)
        {
            if (!TryReadJson(entry.Value, out var items, out var encoded))
            {
                Warn(FlattenWarningCodes.MalformedCollection, entry.Key);
                return;
            }

            if (items.ValueKind is JsonValueKind.Undefined)
            {
                return;
            }

            if (items.ValueKind != JsonValueKind.Array)
            {
                Warn(FlattenWarningCodes.MalformedCollection, entry.Key);
                return;
            }

            var ordinal = 0;
            foreach (var item in items.EnumerateArray())
            {
                var position = ordinal++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Warn(FlattenWarningCodes.MalformedCollectionItem, $"{entry.Key}[{position}]");
                    continue;
                }

                var itemId = ReadItemId(item, encoded);
                if (!TryOccurrence(collection, itemId, position, out var occurrence))
                {
                    continue;
                }

                foreach (var property in item.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!collection.TryGetNested(property.Name, out var nested))
                    {
                        WarnUnmatchedNested(collection, property.Name);
                        continue;
                    }

                    AddValue(nested, property.Value, null, entry.Completed, occurrence, itemId, position, encoded);
                }
            }
        }

        private void AddDerived(CatalogCollection collection, bool isStatus, string itemId, ResponseEntry entry)
        {
            if (!TryOccurrence(collection, itemId, null, out var occurrence))
            {
                return;
            }

            if (isStatus)
            {
                if (collection.StatusFieldId is not null && collection.TryGetNested(collection.StatusFieldId, out var status))
                {
                    AddValue(status, entry.Value, null, entry.Completed, occurrence, itemId, null);
                }

                return;
            }

            if (!TryReadJson(entry.Value, out var data, out var encoded) || data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            {
                Warn(FlattenWarningCodes.MalformedCollection, entry.Key);
                return;
            }

            if (data.ValueKind == JsonValueKind.Undefined)
            {
                return;
            }

            foreach (var property in data.EnumerateObject())
            {
                if (!collection.TryGetNested(property.Name, out var nested))
                {
                    WarnUnmatchedNested(collection, property.Name);
                    continue;
                }

                AddValue(nested, property.Value, null, entry.Completed, occurrence, itemId, null, encoded);
            }
        }

        private void AddValue(
            CatalogField field,
            JsonElement value,
            string? envelopeDataType,
            bool? completed,
            string occurrencePath,
            string? itemId,
            int? itemOrdinal,
            bool encoded = true)
        {
            if (!policy.IsExported(field))
            {
                Withheld++;
                return;
            }

            // The template type wins: envelopes saved without a template infer a type from the value,
            // which would turn a phone number into a decimal.
            var dataType = field.DataType ?? envelopeDataType;
            foreach (var part in ValueInterpreter.Interpret(field, value, dataType, encoded))
            {
                var key = LogicalKey.Compose(field.ParentFieldId, occurrencePath, field.FieldId, part.NestedPath);
                if (!keys.Add(key))
                {
                    Warn(FlattenWarningCodes.DuplicateLogicalKey, key.Replace('\u001F', '|'));
                    continue;
                }

                Facts.Add(new AnswerFact(
                    LogicalKey.Hash(field.ParentFieldId, occurrencePath, field.FieldId, part.NestedPath),
                    field.FieldId,
                    field.ParentFieldId,
                    occurrencePath,
                    itemId,
                    itemOrdinal,
                    part.NestedPath,
                    part.DataType ?? dataType,
                    completed,
                    part.Status,
                    part.ValueString,
                    part.ValueDecimal,
                    part.ValueBool,
                    part.ValueDate,
                    part.ValueDateTime,
                    part.ValueJson,
                    part.RawValue));
            }
        }

        private bool TryOccurrence(CatalogCollection collection, string? itemId, int? ordinal, out string occurrence)
        {
            var segment = string.IsNullOrWhiteSpace(itemId)
                ? PathSegment.Synthetic(ordinal ?? 0)
                : PathSegment.Escape(itemId);
            occurrence = OccurrencePath.Append(string.Empty, collection.Field.FieldId, segment);

            if ((itemId?.Length ?? 0) > MaxItemIdLength || occurrence.Length > OccurrencePath.MaxLength)
            {
                Warn(FlattenWarningCodes.PathTooLong, occurrence);
                return false;
            }

            return true;
        }

        private bool TryMatchDerived(string key, out CatalogCollection collection, out bool isStatus, out string itemId)
        {
            collection = null!;
            isStatus = false;
            itemId = string.Empty;
            var matchedLength = -1;

            foreach (var candidate in derived)
            {
                var prefix = candidate.Field.FieldId;
                if (prefix.Length <= matchedLength || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rest = key.AsSpan(prefix.Length);
                bool status;
                if (rest.StartsWith(StatusInfix, StringComparison.OrdinalIgnoreCase))
                {
                    status = true;
                }
                else if (rest.StartsWith(DataInfix, StringComparison.OrdinalIgnoreCase))
                {
                    status = false;
                }
                else
                {
                    continue;
                }

                var id = rest[(status ? StatusInfix.Length : DataInfix.Length)..].ToString();
                if (id.Length == 0)
                {
                    continue;
                }

                collection = candidate;
                isStatus = status;
                itemId = id;
                matchedLength = prefix.Length;
            }

            return matchedLength >= 0;
        }

        private bool IsNoise(string key) =>
            FormPlumbing.IsPlumbing(key, id => catalogue.TryGetTopLevel(id, out _))
            || (key.EndsWith(CompletedSuffix, StringComparison.OrdinalIgnoreCase)
                && catalogue.IsTaskId(key[..^CompletedSuffix.Length]));

        private void WarnUnmatchedNested(CatalogCollection collection, string key)
        {
            var code = FormPlumbing.IsPlumbing(key, id => collection.TryGetNested(id, out _))
                ? FlattenWarningCodes.NoiseKey
                : FlattenWarningCodes.UnknownNestedField;
            Warn(code, $"{collection.Field.FieldId}.{key}");
        }

        private void Warn(string code, string key) => Warnings.Add(new FlattenWarning(code, key));

        /// <summary>Reads a collection value. An empty value reads as <see cref="JsonValueKind.Undefined"/>.</summary>
        private static bool TryReadJson(JsonElement value, out JsonElement json, out bool encoded)
        {
            encoded = true;
            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                case JsonValueKind.Object:
                    json = value;
                    return true;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    json = default;
                    return true;
                case JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()):
                    json = default;
                    return true;
                case JsonValueKind.String:
                    return ValueInterpreter.TryParseStoredJson(value.GetString()!, out json, out encoded);
                default:
                    json = default;
                    return false;
            }
        }

        private static string? ReadItemId(JsonElement item, bool encoded)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind switch
                    {
                        JsonValueKind.String => encoded ? StoredText.Decode(property.Value.GetString()!) : property.Value.GetString(),
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => null,
                    };
                }
            }

            return null;
        }
    }
}
