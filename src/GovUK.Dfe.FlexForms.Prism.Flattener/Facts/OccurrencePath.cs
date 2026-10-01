using GovUK.Dfe.FlexForms.Prism.Flattener.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

/// <summary>
/// The canonical ancestry of a collection item: alternating collection field ids and item ids, each escaped and
/// separated by <c>/</c>, for example <c>membersLeaving/4d69b42b-...</c>. Items without an id use a synthetic
/// <c>#ordinal</c> segment.
/// </summary>
public static class OccurrencePath
{
    public const int MaxLength = 1000;

    public static string Append(string parentPath, string collectionFieldId, string itemSegment)
    {
        var segment = PathSegment.Escape(collectionFieldId) + PathSegment.Separator + itemSegment;
        return parentPath.Length == 0 ? segment : parentPath + PathSegment.Separator + segment;
    }

    /// <summary>Splits a path into unescaped (collection field id, item id) pairs.</summary>
    public static IReadOnlyList<(string CollectionFieldId, string ItemId)> Parse(string path)
    {
        if (path.Length == 0)
        {
            return [];
        }

        var segments = path.Split(PathSegment.Separator);
        if (segments.Length % 2 != 0)
        {
            throw new FormatException($"Occurrence path '{path}' does not have an even number of segments.");
        }

        var pairs = new List<(string, string)>(segments.Length / 2);
        for (var i = 0; i < segments.Length; i += 2)
        {
            pairs.Add((PathSegment.Unescape(segments[i]), PathSegment.Unescape(segments[i + 1])));
        }

        return pairs;
    }
}
