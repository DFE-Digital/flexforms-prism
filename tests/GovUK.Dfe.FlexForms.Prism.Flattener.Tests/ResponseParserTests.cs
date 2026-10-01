using System.Text.Json;
using GovUK.Dfe.FlexForms.Prism.Flattener.Responses;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class ResponseParserTests
{
    [Fact]
    public void Unwraps_envelopes()
    {
        var parsed = ResponseParser.Parse("""
            { "name": { "question": "Name", "value": "Ada", "completed": true, "dataType": "string" } }
            """);

        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("name", entry.Key);
        Assert.Equal("Ada", entry.Value.GetString());
        Assert.True(entry.Completed);
        Assert.Equal("string", entry.DataType);
    }

    [Fact]
    public void Older_envelopes_without_a_data_type_are_accepted()
    {
        var entry = Assert.Single(ResponseParser.Parse("""{ "name": { "value": "Ada", "completed": false } }""").Entries);

        Assert.Null(entry.DataType);
        Assert.False(entry.Completed);
    }

    [Fact]
    public void Collection_envelopes_with_nested_field_metadata_are_unwrapped()
    {
        var entry = Assert.Single(ResponseParser.Parse("""
            { "members": { "question": "Members", "value": "[]", "completed": false, "dataType": "array",
                           "fields": { "memberName": { "question": "Name", "dataType": "string" } } } }
            """).Entries);

        Assert.Equal("[]", entry.Value.GetString());
        Assert.Equal("array", entry.DataType);
    }

    [Fact]
    public void Legacy_bare_values_are_kept_as_they_are()
    {
        var parsed = ResponseParser.Parse("""{ "name": "Ada", "school": { "name": "X", "urn": "1" }, "count": 3 }""");

        Assert.Equal(["name", "school", "count"], parsed.Entries.Select(e => e.Key));
        Assert.All(parsed.Entries, e => Assert.Null(e.Completed));
        Assert.Equal(JsonValueKind.Object, parsed.Entries[1].Value.ValueKind);
        Assert.Equal(JsonValueKind.Number, parsed.Entries[2].Value.ValueKind);
    }

    [Theory]
    [InlineData("formData")]
    [InlineData("FormData")]
    [InlineData("data")]
    [InlineData("Data")]
    public void Wrapped_bodies_are_unwrapped(string wrapper)
    {
        var entry = Assert.Single(ResponseParser.Parse($$"""{ "{{wrapper}}": { "name": { "value": "Ada" } } }""").Entries);

        Assert.Equal("name", entry.Key);
    }

    [Fact]
    public void Task_status_entries_are_skipped()
    {
        var parsed = ResponseParser.Parse("""
            { "TaskStatus_about": { "value": "Completed", "completed": true }, "name": { "value": "Ada" } }
            """);

        Assert.Equal(["name"], parsed.Entries.Select(e => e.Key));
    }

    [Fact]
    public void Duplicate_keys_keep_the_first_and_warn()
    {
        var parsed = ResponseParser.Parse("""{ "name": { "value": "Ada" }, "NAME": { "value": "Bob" } }""");

        Assert.Equal("Ada", Assert.Single(parsed.Entries).Value.GetString());
        Assert.Equal(FlattenWarningCodes.DuplicateResponseKey, Assert.Single(parsed.Warnings).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_bodies_have_no_entries(string body)
    {
        Assert.Empty(ResponseParser.Parse(body).Entries);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public void Unreadable_bodies_are_permanent_failures(string body)
    {
        Assert.Throws<ResponseFormatException>(() => ResponseParser.Parse(body));
    }
}
