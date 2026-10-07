using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class ValueInterpretationTests
{
    [Fact]
    public void Text_is_decoded_from_the_sanitisers_html_encoding()
    {
        var fact = TestSupport.Flatten(TestSupport.Body(("name", "O&#x27;Brien &amp; Co<br>Line two &lt;br&gt;")))
            .Single("name");

        Assert.Equal(InterpretationStatus.Ok, fact.InterpretationStatus);
        Assert.Equal("O'Brien & Co\nLine two <br>", fact.ValueString);
        Assert.Equal("O&#x27;Brien &amp; Co<br>Line two &lt;br&gt;", fact.RawValue);
        Assert.Equal("string", fact.DataType);
        Assert.True(fact.IsCompleted);
    }

    [Fact]
    public void The_template_type_wins_over_an_inferred_envelope_type()
    {
        var body = """{ "phone": { "value": "07700900123", "completed": false, "dataType": "number" } }""";

        var fact = TestSupport.Flatten(body).Single("phone");

        Assert.Equal("07700900123", fact.ValueString);
        Assert.Null(fact.ValueDecimal);
        Assert.Equal("string", fact.DataType);
    }

    [Fact]
    public void Dates_are_read_exactly()
    {
        var fact = TestSupport.Flatten(TestSupport.Body(("startDate", "2025-04-03"))).Single("startDate");

        Assert.Equal(InterpretationStatus.Ok, fact.InterpretationStatus);
        Assert.Equal(new DateOnly(2025, 4, 3), fact.ValueDate);
        Assert.Equal("DateTime", fact.DataType);
    }

    [Theory]
    [InlineData("2025-13-01")]
    [InlineData("2025-2-30")]
    [InlineData("03/04/2025")]
    public void Invalid_dates_keep_only_the_raw_value(string stored)
    {
        var fact = TestSupport.Flatten(TestSupport.Body(("startDate", stored))).Single("startDate");

        Assert.Equal(InterpretationStatus.ParseFailed, fact.InterpretationStatus);
        Assert.Null(fact.ValueDate);
        Assert.Equal(stored, fact.RawValue);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-1.5", -1.5)]
    [InlineData("1e3", 1000)]
    [InlineData("0.123456789012345", 0.1234567890)]
    public void Numbers_are_invariant_decimals_rounded_to_ten_places(string stored, double expected)
    {
        var fact = TestSupport.Flatten(TestSupport.Body(("pupils", stored))).Single("pupils");

        Assert.Equal(InterpretationStatus.Ok, fact.InterpretationStatus);
        Assert.Equal((decimal)expected, fact.ValueDecimal);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("lots")]
    [InlineData("1e30")]
    public void Unreadable_or_out_of_range_numbers_fail_to_parse(string stored)
    {
        var fact = TestSupport.Flatten(TestSupport.Body(("pupils", stored))).Single("pupils");

        Assert.Equal(InterpretationStatus.ParseFailed, fact.InterpretationStatus);
        Assert.Null(fact.ValueDecimal);
    }

    [Fact]
    public void Empty_and_null_values_are_flagged()
    {
        var result = TestSupport.Flatten("""{ "name": { "value": "" }, "pupils": { "value": null } }""");

        Assert.Equal(InterpretationStatus.Empty, result.Single("name").InterpretationStatus);
        Assert.Equal(InterpretationStatus.Null, result.Single("pupils").InterpretationStatus);
    }

    [Fact]
    public void A_single_checkbox_is_a_plain_code()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("colours", "red")));

        Assert.Equal("""["red"]""", result.Single("colours").ValueJson);
        var code = result.Single("colours", "red");
        Assert.Equal("red", code.ValueString);
        Assert.True(code.ValueBool);
    }

    [Fact]
    public void Several_checkboxes_are_a_json_array_of_codes()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("colours", """["red","blue"]""")));

        Assert.Equal("""["red","blue"]""", result.Single("colours").ValueJson);
        Assert.Equal(["", "red", "blue"], result.Facts.Where(f => f.FieldId == "colours").Select(f => f.NestedPath));
    }

    [Fact]
    public void Checkbox_codes_are_escaped_in_the_nested_path()
    {
        var result = TestSupport.Flatten(TestSupport.Body(("colours", """["a/b","50%"]""")));

        Assert.Equal("a/b", result.Single("colours", "a%2Fb").ValueString);
        Assert.Equal("50%", result.Single("colours", "50%25").ValueString);
    }

    [Fact]
    public void Autocomplete_objects_are_kept_whole_and_promoted_by_property()
    {
        var stored = "{&quot;urn&quot;:&quot;101251&quot;,&quot;name&quot;:&quot;St Mary&#x27;s&quot;,&quot;open&quot;:true,&quot;pupils&quot;:210}";

        var result = TestSupport.Flatten(TestSupport.Body(("school", stored)));

        var whole = result.Single("school");
        Assert.Equal("St Mary's", whole.ValueString);
        Assert.Equal("""{"name":"St Mary's","open":true,"pupils":210,"urn":"101251"}""", whole.ValueJson);
        Assert.Equal("101251", result.Single("school", "urn").ValueString);
        Assert.True(result.Single("school", "open").ValueBool);
        Assert.Equal(210m, result.Single("school", "pupils").ValueDecimal);
    }

    [Fact]
    public void Multiple_autocomplete_selections_are_kept_by_position()
    {
        var stored = """[{"name":"One","urn":"1"},{"name":"Two","urn":"2"}]""";

        var result = TestSupport.Flatten(TestSupport.Body(("school", stored)));

        Assert.Equal("One", result.Single("school", "#0").ValueString);
        Assert.Equal("2", result.Single("school", "#1/urn").ValueString);
    }

    [Fact]
    public void Uploads_produce_one_metadata_fact_per_file()
    {
        var stored = """
            [{"id":"f1","applicationId":"a","uploadedBy":"u","name":"Plan.pdf","originalFileName":"Plan.pdf",
              "fileName":"9567fb.pdf","fileSize":1024,"uploadedOn":"2025-12-15T14:19:39.69"},
             {"id":"f2","name":"b.png","originalFileName":"b.png","fileName":"x.png","fileSize":5,"uploadedOn":"2025-12-15T14:20:00"}]
            """;

        var result = TestSupport.Flatten(TestSupport.Body(("evidence", stored)));

        var first = result.Single("evidence", "f1");
        Assert.Equal("Plan.pdf", first.ValueString);
        Assert.Equal(
            """{"fileSize":1024,"id":"f1","name":"Plan.pdf","originalFileName":"Plan.pdf","uploadedOn":"2025-12-15T14:19:39.69"}""",
            first.ValueJson);
        Assert.Null(first.RawValue);
        Assert.Equal("b.png", result.Single("evidence", "f2").ValueString);
        Assert.DoesNotContain(result.Facts, f => f.FieldId == "evidence" && f.NestedPath == string.Empty);
    }

    [Fact]
    public void An_empty_upload_list_is_empty()
    {
        var fact = TestSupport.Flatten(TestSupport.Body(("evidence", "[]"))).Single("evidence");

        Assert.Equal(InterpretationStatus.Empty, fact.InterpretationStatus);
    }

    [Fact]
    public void Structured_values_on_scalar_fields_are_unsupported_but_kept()
    {
        var fact = TestSupport.Flatten("""{ "name": { "value": { "b": 1, "a": 2 } } }""").Single("name");

        Assert.Equal(InterpretationStatus.Unsupported, fact.InterpretationStatus);
        Assert.Equal("""{"a":2,"b":1}""", fact.ValueJson);
    }

    [Fact]
    public void Legacy_bare_values_are_interpreted()
    {
        var result = TestSupport.Flatten("""{ "name": "Ada", "pupils": 12 }""");

        Assert.Equal("Ada", result.Single("name").ValueString);
        Assert.Null(result.Single("name").IsCompleted);
        Assert.Equal(12m, result.Single("pupils").ValueDecimal);
    }
}
