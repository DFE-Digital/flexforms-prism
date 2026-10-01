using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class ExportPolicyTests
{
    private static readonly string Body = TestSupport.Body(
        ("name", "Ada"),
        ("phone", "0123"),
        ("members", """[{"memberName":"M","joined":"2024-01-01","id":"1"}]"""));

    [Fact]
    public void Nothing_is_exported_without_a_decision()
    {
        var result = TestSupport.Flatten(Body, policy: ExportPolicy.DenyAll);

        Assert.Empty(result.Facts);
        Assert.Equal(4, result.WithheldAnswerCount);
    }

    [Fact]
    public void Only_allowed_fields_become_facts()
    {
        var policy = new ExportPolicy(3,
        [
            new ExportRule("", "name", ExportDecision.Allowed),
            new ExportRule("", "phone", ExportDecision.Denied),
        ]);

        var result = TestSupport.Flatten(Body, policy: policy);

        Assert.Equal(["name"], result.Facts.Select(f => f.FieldId));
        Assert.Equal(3, result.WithheldAnswerCount);
    }

    [Fact]
    public void Nested_fields_also_need_their_collection_to_be_allowed()
    {
        var nestedOnly = new ExportPolicy(1, [new ExportRule("members", "memberName", ExportDecision.Allowed)]);
        var withCollection = new ExportPolicy(1,
        [
            new ExportRule("", "members", ExportDecision.Allowed),
            new ExportRule("members", "memberName", ExportDecision.Allowed),
        ]);

        Assert.Empty(TestSupport.Flatten(Body, policy: nestedOnly).Facts);
        Assert.Equal(["memberName"], TestSupport.Flatten(Body, policy: withCollection).Facts.Select(f => f.FieldId));
    }

    [Fact]
    public void Status_reports_unclassified_allowed_and_denied()
    {
        var policy = new ExportPolicy(1,
        [
            new ExportRule("", "name", ExportDecision.Allowed),
            new ExportRule("", "phone", ExportDecision.Denied),
        ]);

        Assert.Equal(ExportStatus.Allowed, policy.StatusOf("", "NAME"));
        Assert.Equal(ExportStatus.Denied, policy.StatusOf("", "phone"));
        Assert.Equal(ExportStatus.Unclassified, policy.StatusOf("", "startDate"));
        Assert.Equal(ExportStatus.Unclassified, policy.StatusOf("members", "name"));
    }
}
