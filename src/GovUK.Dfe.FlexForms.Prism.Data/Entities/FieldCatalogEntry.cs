namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// Metadata for every field of a template version, with no answer values. Fields that are not allowed
/// by the export policy are still catalogued so admins can see what needs classifying.
/// </summary>
public class FieldCatalogEntry
{
    public Guid TenantId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public string FieldId { get; set; } = string.Empty;
    public int ContractVersion { get; set; }
    public Guid TemplateId { get; set; }
    public string? TemplateVersionNumber { get; set; }
    public string? ParentFieldId { get; set; }
    public string? FlowId { get; set; }
    public string? FlowMode { get; set; }
    public string? TaskGroupId { get; set; }
    public string? TaskGroupName { get; set; }
    public string? TaskId { get; set; }
    public string? TaskName { get; set; }
    public string? PageId { get; set; }
    public string? PageTitle { get; set; }
    public int? FieldOrder { get; set; }
    public string? Label { get; set; }
    public string? DataType { get; set; }
    public string? ControlType { get; set; }
    public bool IsCollection { get; set; }
    public bool? IsRequired { get; set; }
    public string? ChoicesJson { get; set; }
    public string? Sensitivity { get; set; }
    public string? SemanticKey { get; set; }
    public ExportStatus ExportStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}
