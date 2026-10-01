using System.Security.Cryptography;
using System.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

/// <summary>
/// The identity of a fact within a generation. The components are joined with the unit separator, which never
/// appears in field ids, and an occurrence or nested path can't contain it unescaped.
/// </summary>
public static class LogicalKey
{
    private const char UnitSeparator = '\u001F';

    public static string Compose(string parentFieldId, string occurrencePath, string fieldId, string nestedPath) =>
        string.Join(UnitSeparator, parentFieldId, occurrencePath, fieldId, nestedPath);

    public static byte[] Hash(string parentFieldId, string occurrencePath, string fieldId, string nestedPath) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Compose(parentFieldId, occurrencePath, fieldId, nestedPath)));
}
