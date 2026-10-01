using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class CollectionFlatteningTests
{
    private const string Members = """
        [{"memberName":"Ada &amp; Bob","joined":"2024-01-31","id":"m-1"},
         {"memberName":"Cy","joined":"bad","id":"m/2"}]
        """;

    [Fact]
    public void Multi_collection_items_become_facts_under_their_occurrence()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("members", Members)));

        var name = result.Single("memberName", occurrencePath: "members/m-1");
        Assert.Equal("members", name.ParentFieldId);
        Assert.Equal("m-1", name.ItemId);
        Assert.Equal(0, name.ItemOrdinal);
        Assert.Equal("Ada & Bob", name.ValueString);
        Assert.Equal(new DateOnly(2024, 1, 31), result.Single("joined", occurrencePath: "members/m-1").ValueDate);

        var second = result.Single("joined", occurrencePath: "members/m%2F2");
        Assert.Equal("m/2", second.ItemId);
        Assert.Equal(1, second.ItemOrdinal);
        Assert.Equal(InterpretationStatus.ParseFailed, second.InterpretationStatus);
    }

    [Fact]
    public void Items_without_an_id_use_their_position()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("members", """[{"memberName":"Ada"}]""")));

        var fact = result.Single("memberName", occurrencePath: "members/#0");
        Assert.Null(fact.ItemId);
    }

    [Fact]
    public void Unknown_nested_fields_are_reported()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("members", """[{"surprise":"x","id":"1"}]""")));

        Assert.Empty(result.Facts);
        Assert.Contains(result.Warnings, w => w.Code == FlattenWarningCodes.UnknownNestedField && w.Key == "members.surprise");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"memberName":"x"}""")]
    public void Malformed_collections_are_reported(string stored)
    {
        var result = TestSupport.Flatten(TestSupport.Body(("members", stored)));

        Assert.Empty(result.Facts);
        Assert.Contains(result.Warnings, w => w.Code == FlattenWarningCodes.MalformedCollection);
    }

    [Fact]
    public void Collections_posted_with_whole_value_encoding_are_read()
    {
        var stored = "[{&quot;memberName&quot;:&quot;Ada&quot;,&quot;id&quot;:&quot;1&quot;}]";

        var result = TestSupport.Flatten(TestSupport.Body(("members", stored)));

        Assert.Equal("Ada", result.Single("memberName", occurrencePath: "members/1").ValueString);
    }

    [Fact]
    public void Derived_items_are_read_from_status_and_data_keys()
    {
        var result = TestSupport.Flatten(TestSupport.Body(
            ("decl_status_st-marys", "Signed"),
            ("decl_data_st-marys", """{"chair":"Dr O&#x27;Neil"}"""),
            ("decl-leaving_status_st-marys", "Signed"),
            ("decl-leaving_data_st-marys", """{"chair-leaving":"Ms Y"}""")));

        var status = result.Single("status", occurrencePath: "decl/st-marys");
        Assert.Equal("decl", status.ParentFieldId);
        Assert.Equal("st-marys", status.ItemId);
        Assert.Null(status.ItemOrdinal);
        Assert.Equal("Signed", status.ValueString);
        Assert.Equal("Dr O'Neil", result.Single("chair", occurrencePath: "decl/st-marys").ValueString);
        Assert.Equal("Signed", result.Single("status", occurrencePath: "decl-leaving/st-marys").ValueString);
        Assert.Equal("Ms Y", result.Single("chair-leaving", occurrencePath: "decl-leaving/st-marys").ValueString);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Nested_fields_leaked_to_the_top_level_are_skipped()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("chair", "stale"), ("memberName", "stale")));

        Assert.Empty(result.Facts);
        Assert.All(result.Warnings, w => Assert.Equal(FlattenWarningCodes.NestedFieldAtTopLevel, w.Code));
        Assert.Equal(2, result.Warnings.Count);
    }

    [Theory]
    [InlineData("__RequestVerificationToken")]
    [InlineData("TaskId")]
    [InlineData("IsTaskCompleted")]
    [InlineData("about_completed")]
    [InlineData("handler")]
    [InlineData("Data[school]")]
    [InlineData("CurrentTask.TaskName")]
    [InlineData("school_query")]
    public void Form_plumbing_keys_are_noise(string key)
    {
        var result = TestSupport.Flatten(TestSupport.Body((key, "x")));

        Assert.Empty(result.Facts);
        Assert.Equal(FlattenWarningCodes.NoiseKey, Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public void Form_plumbing_inside_collection_items_is_noise()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("members",
            """[{"memberName":"A","memberName_query":"A","handler":"Page","Data[memberName]":"A","id":"1"}]""")));

        Assert.Equal("A", result.Single("memberName", occurrencePath: "members/1").ValueString);
        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal(FlattenWarningCodes.NoiseKey, w.Code));
    }

    [Fact]
    public void A_query_suffix_is_only_noise_beside_its_field()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("nothing_query", "x")));

        Assert.Equal(FlattenWarningCodes.UnknownKey, Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public void Unknown_keys_are_reported()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("somethingElse", "x"), ("decl_other_x", "y")));

        Assert.Empty(result.Facts);
        Assert.All(result.Warnings, w => Assert.Equal(FlattenWarningCodes.UnknownKey, w.Code));
    }

    [Fact]
    public void Duplicate_logical_keys_keep_the_first_fact()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("members", """[{"memberName":"A","id":"1"},{"memberName":"B","id":"1"}]""")));

        Assert.Equal("A", result.Single("memberName", occurrencePath: "members/1").ValueString);
        Assert.Contains(result.Warnings, w => w.Code == FlattenWarningCodes.DuplicateLogicalKey);
    }
}
