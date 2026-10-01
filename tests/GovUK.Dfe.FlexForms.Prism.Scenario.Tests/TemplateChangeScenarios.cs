using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>Published template versions are catalogued straight away, and what changed is visible per version.</summary>
public sealed class TemplateChangeScenarios(SqlServerFixture sql) : ScenarioBase(sql)
{
    private static readonly string NextTemplate = ScenarioHarness.Template
        .Replace("""{ "value": "Name" }""", """{ "value": "Full name" }""", StringComparison.Ordinal)
        .Replace(
            """{ "fieldId": "pupils", "type": "number", "label": { "value": "Pupils" }, "order": 2 },""",
            """{ "fieldId": "email", "type": "text", "label": { "value": "Email" }, "order": 2 },""",
            StringComparison.Ordinal)
        .Replace("\"secret\", \"type\": \"text\"", "\"secret\", \"type\": \"number\"", StringComparison.Ordinal);

    [Fact]
    public async Task The_first_version_lists_every_field_as_added()
    {
        await Prism.DeliverAsync(Source.InitialTemplateVersion());

        var changes = await Prism.FieldChangesAsync(Source.TemplateVersionId);

        Assert.Equal(
            [("", "members"), ("", "name"), ("", "pupils"), ("", "secret"), ("members", "joined"), ("members", "memberName")],
            changes.Select(c => (c.ParentFieldId, c.FieldId)));
        Assert.All(changes, c =>
        {
            Assert.Equal("Added", c.ChangeType);
            Assert.Null(c.PreviousTemplateVersionId);
        });
    }

    [Fact]
    public async Task A_new_version_shows_added_removed_and_changed_fields_with_their_export_decisions()
    {
        await Prism.DeliverAsync(Source.InitialTemplateVersion());
        var published = Source.PublishTemplateVersion("2.0", NextTemplate);

        await Prism.DeliverAsync(published);

        var changes = await Prism.FieldChangesAsync(published.TemplateVersionId);
        Assert.All(changes, c => Assert.Equal(Source.TemplateVersionId, c.PreviousTemplateVersionId));
        Assert.Equal(
            [
                ("email", "Added", false, false, "Unclassified"),
                ("name", "Changed", true, false, "Allowed"),
                ("pupils", "Removed", false, false, "Allowed"),
                ("secret", "Changed", false, true, "Unclassified"),
            ],
            changes.Select(c => (c.FieldId, c.ChangeType, c.LabelChanged, c.TypeChanged, c.ExportDecision)));

        var renamed = changes.Single(c => c.FieldId == "name");
        Assert.Equal(("Name", "Full name"), (renamed.PreviousLabel, renamed.Label));
    }

    [Fact]
    public async Task A_field_added_by_a_new_version_can_be_classified_before_any_application_uses_it()
    {
        await Prism.DeliverAsync(Source.InitialTemplateVersion());
        var published = Source.PublishTemplateVersion("2.0", NextTemplate);
        await Prism.DeliverAsync(published);

        var result = await Prism.ChangePolicyAsync(new ExportDecisionRequest(null, "email", ExportDecision.Allowed, "contact details"));

        Assert.Equal(ApplyStatus.Applied, result.Status);
        var email = Assert.Single(await Prism.FieldChangesAsync(published.TemplateVersionId), c => c.FieldId == "email");
        Assert.Equal("Allowed", email.ExportDecision);
    }

    [Fact]
    public async Task Redelivering_a_template_event_changes_nothing()
    {
        var published = Source.InitialTemplateVersion();

        await Prism.DeliverAsync(published);
        await Prism.DeliverAsync(published);

        Assert.Equal(6, (await Prism.CatalogAsync()).Count);
        Assert.Equal(6, (await Prism.FieldChangesAsync(Source.TemplateVersionId)).Count);
    }
}
