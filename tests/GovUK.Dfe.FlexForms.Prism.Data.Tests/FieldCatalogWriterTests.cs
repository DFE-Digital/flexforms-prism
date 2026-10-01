using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class FieldCatalogWriterTests(SqlServerFixture sql)
{
    private const string Template = """
        {
          "templateId": "tpl", "templateName": "Sample", "description": "",
          "taskGroups": [{
            "groupId": "g1", "groupName": "Group", "groupOrder": 1, "groupStatus": "NotStarted",
            "tasks": [{
              "taskId": "about", "taskName": "About", "taskOrder": 1, "taskStatus": "NotStarted",
              "pages": [{
                "pageId": "p1", "slug": "p1", "title": "Page one", "description": "", "pageOrder": 1,
                "fields": [
                  { "fieldId": "name", "type": "text", "label": { "value": "Name" }, "order": 1 },
                  { "fieldId": "colour", "type": "radios", "label": { "value": "Colour" }, "order": 2,
                    "options": [{ "value": "red", "label": "Red" }, { "value": "blue", "label": "Blue" }] }
                ]
              }]
            }]
          }]
        }
        """;

    private static readonly TemplateCatalogue Catalogue = TemplateCatalogueBuilder.Build(Template);

    private readonly CatalogueSource _source = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2.1");

    private async Task Ensure(ExportPolicy policy)
    {
        await using var db = sql.CreateContext();
        await new FieldCatalogWriter(db, TimeProvider.System).EnsureAsync(_source, Catalogue, policy, default);
    }

    private async Task<List<FieldCatalogEntry>> Entries()
    {
        await using var db = sql.CreateContext();
        return await db.FieldCatalog.AsNoTracking()
            .Where(c => c.TenantId == _source.TenantId && c.TemplateVersionId == _source.TemplateVersionId)
            .OrderBy(c => c.FieldOrder)
            .ToListAsync();
    }

    [Fact]
    public async Task Catalogues_every_field_with_its_metadata()
    {
        await Ensure(ExportPolicy.DenyAll);

        var entries = await Entries();

        Assert.Equal(["name", "colour"], entries.Select(e => e.FieldId));
        Assert.All(entries, e =>
        {
            Assert.Equal(_source.TemplateId, e.TemplateId);
            Assert.Equal("2.1", e.TemplateVersionNumber);
            Assert.Equal(PrismVersions.ContractVersion, e.ContractVersion);
            Assert.Equal(ExportStatus.Unclassified, e.ExportStatus);
        });
        Assert.Equal("""[{"value":"red","label":"Red"},{"value":"blue","label":"Blue"}]""", entries[1].ChoicesJson);
        Assert.Null(entries[0].ChoicesJson);
    }

    [Fact]
    public async Task Ensuring_again_updates_export_status_without_duplicating_rows()
    {
        await Ensure(ExportPolicy.DenyAll);
        await Ensure(new ExportPolicy(3, [
            new ExportRule("", "name", ExportDecision.Allowed),
            new ExportRule("", "colour", ExportDecision.Denied)]));

        var entries = await Entries();

        Assert.Equal([ExportStatus.Allowed, ExportStatus.Denied], entries.Select(e => e.ExportStatus));
    }

    [Fact]
    public async Task Concurrent_ensures_for_one_template_version_both_succeed()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Ensure(ExportPolicy.DenyAll)));

        Assert.Equal(2, (await Entries()).Count);
    }
}
