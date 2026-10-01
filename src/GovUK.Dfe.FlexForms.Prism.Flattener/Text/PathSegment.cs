using System.Security.Cryptography;
using System.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Text;

/// <summary>
/// Escaping for one segment of an occurrence or nested path. <c>%</c>, <c>/</c> and <c>#</c> are percent-encoded
/// so that <c>/</c> only ever separates segments and a leading <c>#</c> only ever marks a synthetic segment.
/// </summary>
public static class PathSegment
{
    public const char Separator = '/';
    public const char SyntheticMarker = '#';

    public static string Escape(string segment)
    {
        if (segment.AsSpan().IndexOfAny('%', Separator, SyntheticMarker) < 0)
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length + 8);
        foreach (var c in segment)
        {
            _ = c switch
            {
                '%' => builder.Append("%25"),
                Separator => builder.Append("%2F"),
                SyntheticMarker => builder.Append("%23"),
                _ => builder.Append(c),
            };
        }

        return builder.ToString();
    }

    public static string Unescape(string segment)
    {
        if (segment.IndexOf('%') < 0)
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '%' && i + 2 < segment.Length)
            {
                var code = segment.Substring(i + 1, 2);
                char? decoded = code.ToUpperInvariant() switch
                {
                    "25" => '%',
                    "2F" => Separator,
                    "23" => SyntheticMarker,
                    _ => null,
                };

                if (decoded is { } value)
                {
                    builder.Append(value);
                    i += 2;
                    continue;
                }
            }

            builder.Append(segment[i]);
        }

        return builder.ToString();
    }

    /// <summary>A synthetic segment for positions that have no natural identifier, such as an item without an id.</summary>
    public static string Synthetic(int ordinal) => SyntheticMarker + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Keeps a path within <paramref name="maxLength"/>. Longer paths are replaced by a stable digest so the
    /// logical key stays unique and deterministic.
    /// </summary>
    public static string Fit(string path, int maxLength)
    {
        if (path.Length <= maxLength)
        {
            return path;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return SyntheticMarker + "sha256:" + Convert.ToHexStringLower(digest);
    }
}
