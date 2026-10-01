using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Text;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class PathEncodingTests
{
    private const string Alphabet = "ab/%#2F_- é";

    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 20)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Escaped_segments_round_trip_and_never_contain_a_separator(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 200; i++)
        {
            var segment = RandomText(random);

            var escaped = PathSegment.Escape(segment);

            Assert.DoesNotContain(PathSegment.Separator, escaped);
            Assert.False(escaped.StartsWith(PathSegment.SyntheticMarker));
            Assert.Equal(segment, PathSegment.Unescape(escaped));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Occurrence_paths_round_trip(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 100; i++)
        {
            var collection = "c" + RandomText(random);
            var item = "i" + RandomText(random);
            var nestedCollection = "n" + RandomText(random);
            var nestedItem = "j" + RandomText(random);

            var path = OccurrencePath.Append(
                OccurrencePath.Append(string.Empty, collection, PathSegment.Escape(item)),
                nestedCollection,
                PathSegment.Escape(nestedItem));

            Assert.Equal([(collection, item), (nestedCollection, nestedItem)], OccurrencePath.Parse(path));
        }
    }

    [Fact]
    public void Distinct_segments_never_collide()
    {
        var random = new Random(42);
        var segments = Enumerable.Range(0, 2000).Select(_ => RandomText(random)).Distinct().ToList();

        Assert.Equal(segments.Count, segments.Select(PathSegment.Escape).Distinct().Count());
    }

    [Fact]
    public void Real_ids_cannot_collide_with_synthetic_positions()
    {
        Assert.NotEqual(PathSegment.Synthetic(0), PathSegment.Escape("#0"));
    }

    [Fact]
    public void Overlong_paths_are_replaced_by_a_stable_digest()
    {
        var path = new string('x', 400);

        var fitted = PathSegment.Fit(path, 300);

        Assert.True(fitted.Length <= 300);
        Assert.StartsWith("#sha256:", fitted, StringComparison.Ordinal);
        Assert.Equal(fitted, PathSegment.Fit(path, 300));
        Assert.Equal("short", PathSegment.Fit("short", 300));
    }

    [Fact]
    public void Logical_keys_separate_their_components()
    {
        Assert.NotEqual(LogicalKey.Hash("a", "", "bc", ""), LogicalKey.Hash("ab", "", "c", ""));
    }

    private static string RandomText(Random random)
    {
        var length = random.Next(0, 12);
        return new string(Enumerable.Range(0, length).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray());
    }
}
