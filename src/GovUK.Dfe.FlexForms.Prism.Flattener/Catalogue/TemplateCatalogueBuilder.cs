using System.Text.Json;

namespace GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

/// <summary>
/// Builds the field catalogue of a template version from its JSON, following the web front end's FormTemplate
/// model: task groups, tasks and pages, plus the pages of multi-collection and derived collection flows.
/// When a field id is defined more than once in the same scope the first definition wins, as in the front end.
/// </summary>
public static class TemplateCatalogueBuilder
{
    public const string DerivedStatusControlType = "derivedStatus";
    public const string CollectionControlType = "collection";
    private const string DefaultStatusField = "status";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static TemplateCatalogue Build(string templateJson)
    {
        TemplateModel? template;
        try
        {
            template = JsonSerializer.Deserialize<TemplateModel>(templateJson, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new TemplateFormatException("The template version is not valid template JSON.", ex);
        }

        if (template?.TaskGroups is null)
        {
            throw new TemplateFormatException("The template version has no task groups.");
        }

        return new Builder(template).Build();
    }

    private sealed class Builder(TemplateModel template)
    {
        private readonly List<CatalogField> fields = [];
        private readonly List<CatalogCollection> collections = [];
        private readonly List<string> warnings = [];
        private readonly List<string> taskIds = [];
        private readonly HashSet<string> topLevelIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool requiredByDefault =
            string.Equals(template.DefaultFieldRequirementPolicy?.Trim(), "required", StringComparison.OrdinalIgnoreCase);
        private int order;

        public TemplateCatalogue Build()
        {
            foreach (var group in Ordered(template.TaskGroups, g => g.GroupOrder))
            {
                foreach (var task in Ordered(group.Tasks, t => t.TaskOrder))
                {
                    if (!string.IsNullOrWhiteSpace(task.TaskId))
                    {
                        taskIds.Add(task.TaskId);
                    }

                    var context = new CatalogField
                    {
                        FieldId = string.Empty,
                        TaskGroupId = group.GroupId,
                        TaskGroupName = group.GroupName,
                        TaskId = task.TaskId,
                        TaskName = task.TaskName,
                    };

                    AddTopLevelPages(task.Pages, context);

                    foreach (var flow in task.Summary?.Flows ?? [])
                    {
                        AddCollection(flow.FieldId, flow.FlowId, flow.Title, FlowMode.MultiCollection, flow.Pages, null, context);
                    }

                    foreach (var flow in task.Summary?.DerivedFlows ?? [])
                    {
                        var statusField = string.IsNullOrWhiteSpace(flow.StatusField) ? DefaultStatusField : flow.StatusField.Trim();
                        AddCollection(flow.FieldId, flow.FlowId, flow.Title, FlowMode.DerivedCollection, flow.Pages, statusField, context);
                    }
                }
            }

            return new TemplateCatalogue(template.TemplateId, fields, collections, taskIds, warnings);
        }

        private void AddTopLevelPages(List<PageModel>? pages, CatalogField context)
        {
            foreach (var (page, field) in PageFields(pages))
            {
                if (!topLevelIds.Add(field.FieldId!))
                {
                    warnings.Add($"Duplicate field '{field.FieldId}' in task '{context.TaskId}' was ignored.");
                    continue;
                }

                fields.Add(ToCatalogField(field, page, context, string.Empty));
            }
        }

        private void AddCollection(
            string? storageFieldId,
            string? flowId,
            string? title,
            FlowMode mode,
            List<PageModel>? pages,
            string? statusFieldId,
            CatalogField context)
        {
            if (string.IsNullOrWhiteSpace(storageFieldId))
            {
                warnings.Add($"Flow '{flowId}' in task '{context.TaskId}' has no fieldId and was ignored.");
                return;
            }

            storageFieldId = storageFieldId.Trim();
            if (!topLevelIds.Add(storageFieldId))
            {
                warnings.Add($"Collection '{storageFieldId}' in task '{context.TaskId}' duplicates another field and was ignored.");
                return;
            }

            var orderedPages = Ordered(pages, p => p.PageOrder).ToList();
            var collectionField = context with
            {
                FieldId = storageFieldId,
                FlowId = flowId,
                FlowMode = mode,
                FieldOrder = ++order,
                Label = FlowLabel(title, orderedPages, flowId),
                DataType = FieldDataTypes.Array,
                ControlType = CollectionControlType,
                IsCollection = true,
            };
            fields.Add(collectionField);

            var nested = new List<CatalogField>();
            var nestedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nestedContext = context with { FlowId = flowId, FlowMode = mode };
            foreach (var (page, field) in PageFields(orderedPages))
            {
                if (!nestedIds.Add(field.FieldId!))
                {
                    warnings.Add($"Duplicate field '{field.FieldId}' in collection '{storageFieldId}' was ignored.");
                    continue;
                }

                nested.Add(ToCatalogField(field, page, nestedContext, storageFieldId));
            }

            if (statusFieldId is not null && nestedIds.Add(statusFieldId))
            {
                nested.Add(nestedContext with
                {
                    FieldId = statusFieldId,
                    ParentFieldId = storageFieldId,
                    FieldOrder = ++order,
                    Label = "Status",
                    DataType = FieldDataTypes.String,
                    ControlType = DerivedStatusControlType,
                });
            }

            fields.AddRange(nested);
            collections.Add(new CatalogCollection(collectionField, mode, nested, statusFieldId));
        }

        private CatalogField ToCatalogField(FieldModel field, PageModel page, CatalogField context, string parentFieldId) =>
            context with
            {
                FieldId = field.FieldId!,
                ParentFieldId = parentFieldId,
                PageId = page.PageId,
                PageTitle = page.Title,
                FieldOrder = ++order,
                Label = string.IsNullOrWhiteSpace(field.Label?.Value) ? page.Title : field.Label.Value.Trim(),
                DataType = FieldDataTypes.FromTemplateType(field.Type),
                ControlType = field.Type?.Trim(),
                ComplexFieldId = string.IsNullOrWhiteSpace(field.ComplexField?.Id) ? null : field.ComplexField.Id,
                IsRequired = field.Required ?? requiredByDefault,
                Options = field.Options?
                    .Where(o => o.Value is not null)
                    .Select(o => new CatalogOption(o.Value!, o.Label ?? o.Value!))
                    .ToList() ?? [],
            };

        private static IEnumerable<(PageModel Page, FieldModel Field)> PageFields(IEnumerable<PageModel>? pages)
        {
            foreach (var page in Ordered(pages, p => p.PageOrder))
            {
                foreach (var field in Ordered(page.Fields?.OfType<FieldModel>(), f => f.Order))
                {
                    if (!string.IsNullOrWhiteSpace(field.FieldId))
                    {
                        field.FieldId = field.FieldId.Trim();
                        yield return (page, field);
                    }
                }
            }
        }

        private static string? FlowLabel(string? title, IEnumerable<PageModel> pages, string? flowId)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }

            return pages.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Title))?.Title ?? flowId?.Trim();
        }

        private static IEnumerable<T> Ordered<T>(IEnumerable<T>? items, Func<T, int> key) =>
            items is null ? [] : items.Where(i => i is not null).OrderBy(key);
    }
}
