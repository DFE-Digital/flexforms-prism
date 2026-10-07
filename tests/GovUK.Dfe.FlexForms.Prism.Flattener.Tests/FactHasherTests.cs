using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class FactHasherTests
{
    private static readonly FactHashHeader Header = new(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), 1, 1, 1);

    private const string Body = """
        {
          "name": { "question": "Name", "value": "Ada", "completed": true, "dataType": "string" },
          "colours": { "question": "Colours", "value": "[\"red\",\"blue\"]", "completed": true, "dataType": "string" },
          "members": { "question": "Members", "value": "[{\"memberName\":\"M\",\"id\":\"1\"}]", "completed": false, "dataType": "array" },
          "TaskStatus_about": { "value": "Completed", "completed": true }
        }
        """;

    private const string ReorderedBody = """
        {
          "TaskStatus_about": { "completed": true, "value": "Completed" },
          "members": { "dataType": "array", "completed": false, "value": "[{\"memberName\":\"M\",\"id\":\"1\"}]", "question": "Members" },
          "colours": { "value": "[\"red\",\"blue\"]", "dataType": "string", "question": "Colours", "completed": true },
          "name":    { "completed": true, "dataType": "string", "value": "Ada", "question": "Name" }
        }
        """;

    private static byte[] Hash(string body, FactHashHeader? header = null) =>
        FactHasher.Compute(header ?? Header, TestSupport.Flatten(body).Facts);

    [Fact]
    public void Reordered_json_produces_the_same_hash()
    {
        Assert.Equal(Hash(Body), Hash(ReorderedBody));
    }

    [Fact]
    public void Hash_is_32_bytes_and_stable_across_runs()
    {
        var first = Hash(Body);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, Hash(Body));
    }

    [Fact]
    public void Changing_a_value_changes_the_hash()
    {
        Assert.NotEqual(Hash(Body), Hash(Body.Replace("\"Ada\"", "\"Bob\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Changing_the_completed_flag_changes_the_hash()
    {
        var changed = Body.Replace(
            "\"value\": \"Ada\", \"completed\": true",
            "\"value\": \"Ada\", \"completed\": false",
            StringComparison.Ordinal);

        Assert.NotEqual(Hash(Body), Hash(changed));
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 1, 2)]
    public void Version_changes_change_the_hash(int projector, int contract, int policy)
    {
        Assert.NotEqual(Hash(Body), Hash(Body, Header with { ProjectorVersion = projector, ContractVersion = contract, ExportPolicyVersion = policy }));
    }

    [Fact]
    public void Template_version_changes_the_hash()
    {
        Assert.NotEqual(Hash(Body), Hash(Body, Header with { TemplateVersionId = Guid.NewGuid() }));
    }

    [Fact]
    public void Fact_order_does_not_matter()
    {
        var facts = TestSupport.Flatten(Body).Facts;

        Assert.Equal(FactHasher.Compute(Header, facts), FactHasher.Compute(Header, facts.Reverse()));
    }
}
