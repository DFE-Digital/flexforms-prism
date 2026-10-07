namespace GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

// The subset of the web front end's FormTemplate model (GovUK.Dfe.FlexForms.Domain.Models) that the catalogue
// needs. Templates are read case-insensitively, as the front end does.

internal sealed class TemplateModel
{
    public string? TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public List<TaskGroupModel>? TaskGroups { get; set; }
    public string? DefaultFieldRequirementPolicy { get; set; }
    public List<RetiredFieldModel?>? RetiredFields { get; set; }
}

internal sealed class RetiredFieldModel
{
    public string? FieldId { get; set; }
    public string? ParentFieldId { get; set; }
    public List<string?>? ReplacedBy { get; set; }
}

internal sealed class TaskGroupModel
{
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public int GroupOrder { get; set; }
    public List<TaskModel>? Tasks { get; set; }
}

internal sealed class TaskModel
{
    public string? TaskId { get; set; }
    public string? TaskName { get; set; }
    public int TaskOrder { get; set; }
    public List<PageModel>? Pages { get; set; }
    public TaskSummaryModel? Summary { get; set; }
}

internal sealed class TaskSummaryModel
{
    public string? Mode { get; set; }
    public List<MultiCollectionFlowModel>? Flows { get; set; }
    public List<DerivedCollectionFlowModel>? DerivedFlows { get; set; }
}

internal sealed class MultiCollectionFlowModel
{
    public string? FlowId { get; set; }
    public string? Title { get; set; }
    public string? FieldId { get; set; }
    public string? SemanticKey { get; set; }
    public List<PageModel>? Pages { get; set; }
}

internal sealed class DerivedCollectionFlowModel
{
    public string? FlowId { get; set; }
    public string? Title { get; set; }
    public string? SourceFieldId { get; set; }
    public string? FieldId { get; set; }
    public string? SemanticKey { get; set; }
    public string? StatusField { get; set; }
    public List<PageModel>? Pages { get; set; }
}

internal sealed class PageModel
{
    public string? PageId { get; set; }
    public string? Title { get; set; }
    public int PageOrder { get; set; }
    public List<FieldModel?>? Fields { get; set; }
}

internal sealed class FieldModel
{
    public string? FieldId { get; set; }
    public string? SemanticKey { get; set; }
    public string? Type { get; set; }
    public LabelModel? Label { get; set; }
    public bool? Required { get; set; }
    public int Order { get; set; }
    public List<OptionModel>? Options { get; set; }
    public ComplexFieldModel? ComplexField { get; set; }
}

internal sealed class LabelModel
{
    public string? Value { get; set; }
}

internal sealed class OptionModel
{
    public string? Value { get; set; }
    public string? Label { get; set; }
}

internal sealed class ComplexFieldModel
{
    public string? Id { get; set; }
}
