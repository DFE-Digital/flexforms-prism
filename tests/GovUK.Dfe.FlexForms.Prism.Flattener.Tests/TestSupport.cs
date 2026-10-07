using System.Text.Json;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;
using GovUK.Dfe.FlexForms.Prism.Flattener.Flattening;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Flattener.Responses;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Tests;

internal static class TestSupport
{
    /// <summary>A small template covering each field shape and both collection modes.</summary>
    public const string SampleTemplate = """
        {
          "templateId": "tpl",
          "templateName": "Sample",
          "description": "",
          "defaultFieldRequirementPolicy": "required",
          "taskGroups": [{
            "groupId": "g1", "groupName": "Group", "groupOrder": 1, "groupStatus": "NotStarted",
            "tasks": [
              {
                "taskId": "about", "taskName": "About", "taskOrder": 1, "taskStatus": "NotStarted",
                "pages": [{
                  "pageId": "p1", "slug": "p1", "title": "Page one", "description": "", "pageOrder": 1,
                  "fields": [
                    { "fieldId": "name", "type": "text", "label": { "value": "Name" }, "order": 1 },
                    { "fieldId": "phone", "type": "text", "label": { "value": "Phone" }, "order": 2, "required": false },
                    { "fieldId": "startDate", "type": "date", "label": { "value": "Start" }, "order": 3 },
                    { "fieldId": "colours", "type": "checkboxes", "label": { "value": "Colours" }, "order": 4,
                      "options": [{ "value": "red", "label": "Red" }, { "value": "blue", "label": "Blue" }] },
                    { "fieldId": "school", "type": "complexField", "label": { "value": "School" }, "order": 5,
                      "complexField": { "id": "EstablishmentComplexField" } },
                    { "fieldId": "evidence", "type": "complexField", "label": { "value": "Evidence" }, "order": 6,
                      "complexField": { "id": "UploadDocumentsComplexField" } },
                    { "fieldId": "pupils", "type": "number", "label": { "value": "Pupils" }, "order": 7 },
                    { "fieldId": "name", "type": "text", "label": { "value": "Duplicate" }, "order": 8 }
                  ]
                }]
              },
              {
                "taskId": "people", "taskName": "People", "taskOrder": 2, "taskStatus": "NotStarted",
                "summary": {
                  "mode": "multiCollectionFlow",
                  "flows": [{
                    "flowId": "members", "title": "Members", "fieldId": "members",
                    "pages": [{
                      "pageId": "m1", "slug": "m1", "title": "Member", "description": "", "pageOrder": 1,
                      "fields": [
                        { "fieldId": "memberName", "type": "text", "label": { "value": "Member name" }, "order": 1 },
                        { "fieldId": "joined", "type": "date", "label": { "value": "Joined" }, "order": 2 }
                      ]
                    }]
                  }]
                }
              },
              {
                "taskId": "declarations", "taskName": "Declarations", "taskOrder": 3, "taskStatus": "NotStarted",
                "summary": {
                  "mode": "derivedCollectionFlow",
                  "derivedFlows": [
                    {
                      "flowId": "d1", "title": "Declarations", "sourceFieldId": "members", "fieldId": "decl",
                      "pages": [{
                        "pageId": "d1p", "slug": "d1p", "title": "Declare", "description": "", "pageOrder": 1,
                        "fields": [{ "fieldId": "chair", "type": "text", "label": { "value": "Chair" }, "order": 1 }]
                      }]
                    },
                    {
                      "flowId": "d2", "title": "Leaving", "sourceFieldId": "members", "fieldId": "decl-leaving",
                      "pages": [{
                        "pageId": "d2p", "slug": "d2p", "title": "Declare", "description": "", "pageOrder": 1,
                        "fields": [{ "fieldId": "chair-leaving", "type": "text", "label": { "value": "Chair" }, "order": 1 }]
                      }]
                    }
                  ]
                }
              }
            ]
          }]
        }
        """;

    public static TemplateCatalogue SampleCatalogue => TemplateCatalogueBuilder.Build(SampleTemplate);

    public static ExportPolicy AllowAll(TemplateCatalogue catalogue, int version = 1) =>
        new(version, catalogue.Fields.Select(f => new ExportRule(f.ParentFieldId, f.FieldId, ExportDecision.Allowed)));

    public static FlattenResult Flatten(string body, TemplateCatalogue? catalogue = null, ExportPolicy? policy = null)
    {
        catalogue ??= SampleCatalogue;
        return ResponseFlattener.Flatten(catalogue, ResponseParser.Parse(body), policy ?? AllowAll(catalogue));
    }

    /// <summary>Builds a response body of current-format envelopes from field id and stored string value pairs.</summary>
    public static string Body(params (string Key, string Value)[] entries) =>
        JsonSerializer.Serialize(entries.ToDictionary(
            e => e.Key,
            e => new { question = "", value = e.Value, completed = true, dataType = "string" }));

    public static AnswerFact Single(this FlattenResult result, string fieldId, string nestedPath = "", string occurrencePath = "") =>
        Assert.Single(result.Facts, f => f.FieldId == fieldId && f.NestedPath == nestedPath && f.OccurrencePath == occurrencePath);
}
