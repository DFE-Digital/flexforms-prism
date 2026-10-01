using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

/// <summary>Everything besides the facts that changes what a generation means.</summary>
public sealed record FactHashHeader(Guid TemplateVersionId, int ProjectorVersion, int ContractVersion, int ExportPolicyVersion);

/// <summary>
/// The canonical hash of a projection. Facts are sorted by logical key hash and every field is written
/// length-prefixed in invariant form, so the hash depends only on fact content, never on the order or
/// formatting of the source JSON.
/// </summary>
public static class FactHasher
{
    private const string Format = "prism-facts-v1";

    public static byte[] Compute(FactHashHeader header, IEnumerable<AnswerFact> facts)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Format);
            writer.Write(header.TemplateVersionId.ToByteArray());
            writer.Write(header.ProjectorVersion);
            writer.Write(header.ContractVersion);
            writer.Write(header.ExportPolicyVersion);

            var sorted = facts.OrderBy(f => f.LogicalKeyHash, ByteArrayComparer.Instance).ToList();
            writer.Write(sorted.Count);
            foreach (var fact in sorted)
            {
                WriteFact(writer, fact);
            }
        }

        stream.Position = 0;
        return SHA256.HashData(stream);
    }

    private static void WriteFact(BinaryWriter writer, AnswerFact fact)
    {
        writer.Write(fact.LogicalKeyHash.Length);
        writer.Write(fact.LogicalKeyHash);
        writer.Write(fact.FieldId);
        writer.Write(fact.ParentFieldId);
        writer.Write(fact.OccurrencePath);
        WriteOptional(writer, fact.ItemId);
        WriteOptional(writer, fact.ItemOrdinal?.ToString(CultureInfo.InvariantCulture));
        writer.Write(fact.NestedPath);
        WriteOptional(writer, fact.DataType);
        WriteOptional(writer, fact.IsCompleted?.ToString());
        writer.Write(fact.InterpretationStatus.ToString());
        WriteOptional(writer, fact.ValueString);
        WriteOptional(writer, fact.ValueDecimal?.ToString("0.##########", CultureInfo.InvariantCulture));
        WriteOptional(writer, fact.ValueBool?.ToString());
        WriteOptional(writer, fact.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WriteOptional(writer, fact.ValueDateTime?.ToString("O", CultureInfo.InvariantCulture));
        WriteOptional(writer, fact.ValueJson);
        WriteOptional(writer, fact.RawValue);
    }

    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
