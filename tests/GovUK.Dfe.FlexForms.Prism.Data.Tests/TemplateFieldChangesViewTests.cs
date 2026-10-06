using GovUK.Dfe.FlexForms.Prism.Data.Catalog;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using Microsoft.EntityFrameworkCore;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class TemplateFieldChangesViewTests(SqlServerFixture sql)
{
    private const string Version1 = """
        {
          "templateId": "tpl",
          "taskGroups": [{ "groupId": "g", "groupOrder": 1, "tasks": [{
            "taskId": "t", "taskOrder": 1,
            "pages": [{ "pageId": "p", "title": "Page", "pageOrder": 1, "fields": [
              { "fieldId": "name", "type": "text", "label": { "value": "Name" }, "order": 1 },
              { "fieldId": "email", "type": "email", "label": { "value": "Email" }, "order": 2 }
            ]}],
            "summary": { "flows": [{ "flowId": "f", "title": "People", "fieldId": "attendees", "pages": [{ "pageId": "a", "fields": [
              { "fieldId": "attendeeName", "type": "text", "label": { "value": "Attendee" } }
            ]}]}]}
          }]}]
        }
        """;

    private const string Version2 = """
        {
          "templateId": "tpl",
          "retiredFields": [
            { "fieldId": "name", "replacedBy": ["fullName"] },
            { "fieldId": "email", "replacedBy": ["contactEmail"] },
            { "fieldId": "attendees", "replacedBy": ["visitors"] }
          ],
          "taskGroups": [{ "groupId": "g", "groupOrder": 1, "tasks": [{
            "taskId": "t", "taskOrder": 1,
            "pages": [{ "pageId": "p", "title": "Page", "pageOrder": 1, "fields": [
              { "fieldId": "fullName", "semanticKey": "name", "type": "text", "label": { "value": "Name" }, "order": 1 },
              { "fieldId": "contactEmail", "type": "email", "label": { "value": "Email" }, "order": 2 }
            ]}],
            "summary": { "flows": [{ "flowId": "f", "title": "People", "fieldId": "visitors", "semanticKey": "attendees", "pages": [{ "pageId": "a", "fields": [
              { "fieldId": "attendeeName", "type": "text", "label": { "value": "Attendee" } }
            ]}]}]}
          }]}]
        }
        """;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _templateId = Guid.NewGuid();

    private sealed record ChangeRow(
        string ChangeType, string ParentFieldId, string FieldId, string? PreviousParentFieldId, string? PreviousFieldId,
        string SemanticKey, bool FieldIdChanged, string? ReplacedBy);

    private async Task<Guid> Catalogue(string json, string versionNumber, DateTime createdOn, Guid? existingVersionId = null)
    {
        var versionId = existingVersionId ?? Guid.NewGuid();
        await using var db = sql.CreateContext();
        await new FieldCatalogWriter(db, TimeProvider.System).EnsureAsync(
            new CatalogueSource(_tenantId, _templateId, versionId, versionNumber, createdOn),
            TemplateCatalogueBuilder.Build(json),
            ExportPolicy.DenyAll,
            default);
        return versionId;
    }

    private async Task<List<ChangeRow>> Changes(Guid versionId)
    {
        await using var db = sql.CreateContext();
        return await db.Database.SqlQuery<ChangeRow>($"""
                SELECT change_type AS ChangeType, parent_field_id AS ParentFieldId, field_id AS FieldId,
                       previous_parent_field_id AS PreviousParentFieldId, previous_field_id AS PreviousFieldId,
                       semantic_key AS SemanticKey, field_id_changed AS FieldIdChanged, replaced_by AS ReplacedBy
                FROM prism.v_template_field_changes
                WHERE tenant_id = {_tenantId} AND template_version_id = {versionId}
                """)
            .ToListAsync();
    }

    [Fact]
    public async Task Fields_renamed_under_their_semantic_key_are_one_changed_row_and_retirements_show_their_replacements()
    {
        await Catalogue(Version1, "1.0", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var version2 = await Catalogue(Version2, "2.0", new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));

        var changes = (await Changes(version2)).OrderBy(c => c.SemanticKey).ThenBy(c => c.ChangeType).ToList();

        Assert.Equal(
            [
                new ChangeRow("Changed", "", "visitors", "", "attendees", "attendees", true, null),
                new ChangeRow("Changed", "visitors", "attendeeName", "attendees", "attendeeName", "attendees/attendeeName", true, null),
                new ChangeRow("Added", "", "contactEmail", null, null, "contactEmail", false, null),
                new ChangeRow("Removed", "", "email", "", "email", "email", false, "contactEmail"),
                new ChangeRow("Changed", "", "fullName", "", "name", "name", true, null),
            ],
            changes);
    }

    [Fact]
    public async Task Catalogue_rows_record_the_semantic_key_and_versions_record_their_retirements()
    {
        var version2 = await Catalogue(Version2, "2.0", new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
        await Catalogue(Version2, "2.0", new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), version2);

        await using var db = sql.CreateContext();
        var keys = await db.FieldCatalog.AsNoTracking()
            .Where(c => c.TenantId == _tenantId)
            .ToDictionaryAsync(c => c.ParentFieldId + "/" + c.FieldId, c => c.SemanticKey);
        var retirements = await db.TemplateFieldRetirements.AsNoTracking()
            .Where(r => r.TenantId == _tenantId && r.TemplateVersionId == version2)
            .OrderBy(r => r.FieldId)
            .Select(r => r.FieldId + ":" + r.ReplacedBy)
            .ToListAsync();

        Assert.Equal("name", keys["/fullName"]);
        Assert.Equal("attendees/attendeeName", keys["visitors/attendeeName"]);
        Assert.Equal(["attendees:visitors", "email:contactEmail", "name:fullName"], retirements);
    }
}
