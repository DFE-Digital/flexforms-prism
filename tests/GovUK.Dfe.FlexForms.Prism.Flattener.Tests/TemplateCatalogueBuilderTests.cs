using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

public class TemplateCatalogueBuilderTests
{
    [Fact]
    public void Catalogues_top_level_fields_with_hierarchy_and_types()
    {
        var catalogue = TestSupport.SampleCatalogue;

        Assert.True(catalogue.TryGetTopLevel("startDate", out var field));
        Assert.Equal(string.Empty, field.ParentFieldId);
        Assert.Equal("g1", field.TaskGroupId);
        Assert.Equal("about", field.TaskId);
        Assert.Equal("p1", field.PageId);
        Assert.Equal("Start", field.Label);
        Assert.Equal(FieldDataTypes.DateTime, field.DataType);
        Assert.Equal("date", field.ControlType);
    }

    [Fact]
    public void Required_falls_back_to_the_template_policy()
    {
        var catalogue = TestSupport.SampleCatalogue;

        Assert.True(catalogue.TryGetTopLevel("name", out var name));
        Assert.True(catalogue.TryGetTopLevel("phone", out var phone));
        Assert.True(name.IsRequired);
        Assert.False(phone.IsRequired);
    }

    [Fact]
    public void First_definition_of_a_duplicate_field_wins()
    {
        var catalogue = TestSupport.SampleCatalogue;

        Assert.True(catalogue.TryGetTopLevel("name", out var name));
        Assert.Equal("Name", name.Label);
        Assert.Single(catalogue.Fields, f => f.FieldId == "name");
        Assert.Contains(catalogue.Warnings, w => w.Contains("'name'", StringComparison.Ordinal));
    }

    [Fact]
    public void Lookups_ignore_case()
    {
        Assert.True(TestSupport.SampleCatalogue.TryGetTopLevel("STARTDATE", out _));
    }

    [Fact]
    public void Multi_collection_flows_catalogue_the_collection_and_its_nested_fields()
    {
        var catalogue = TestSupport.SampleCatalogue;

        Assert.True(catalogue.TryGetCollection("members", out var members));
        Assert.Equal(FlowMode.MultiCollection, members.Mode);
        Assert.True(members.Field.IsCollection);
        Assert.Equal(FieldDataTypes.Array, members.Field.DataType);
        Assert.Equal("Members", members.Field.Label);
        Assert.Equal(["memberName", "joined"], members.NestedFields.Select(f => f.FieldId));
        Assert.All(members.NestedFields, f => Assert.Equal("members", f.ParentFieldId));
        Assert.True(catalogue.IsNestedFieldId("memberName"));
    }

    [Fact]
    public void Derived_flows_get_a_synthetic_status_field()
    {
        var catalogue = TestSupport.SampleCatalogue;

        Assert.True(catalogue.TryGetCollection("decl", out var declarations));
        Assert.Equal(FlowMode.DerivedCollection, declarations.Mode);
        Assert.Equal("status", declarations.StatusFieldId);
        Assert.True(declarations.TryGetNested("status", out var status));
        Assert.Equal(TemplateCatalogueBuilder.DerivedStatusControlType, status.ControlType);
        Assert.Equal("decl", status.ParentFieldId);
    }

    [Fact]
    public void Field_order_follows_display_order_across_the_template()
    {
        var orders = TestSupport.SampleCatalogue.Fields.Select(f => f.FieldOrder).ToList();

        Assert.Equal(Enumerable.Range(1, orders.Count), orders);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"taskGroups": "nope"}""")]
    public void Unreadable_templates_are_permanent_failures(string json)
    {
        Assert.Throws<TemplateFormatException>(() => TemplateCatalogueBuilder.Build(json));
    }

    [Fact]
    public void Transfer_template_catalogues_every_flow()
    {
        var catalogue = TemplateCatalogueBuilder.Build(Fixtures.Read("transfer/template.json"));

        string[] expected =
        [
            "detailsOfIncomingTrust", "membersAfterTransfer", "membersLeaving", "trusteesAfterTransfer", "trusteesLeaving",
            "detailsOfAcademies", "detailsOfOutgoingTrusts", "trustDeclarations", "trustDeclarations-leaving",
        ];
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            catalogue.Collections.Select(c => c.Field.FieldId).Order(StringComparer.Ordinal));
        Assert.Empty(catalogue.Warnings);
        Assert.Equal(2, catalogue.Fields.Count(f => f.FieldId == "status"));
    }
}
