namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// An explicit export decision for a field, scoped to the template so it carries across template versions.
/// Fields without a decision are denied.
/// </summary>
public class FieldExportPolicy
{
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public string FieldId { get; set; } = string.Empty;
    public ExportDecision Decision { get; set; }
    public int PolicyVersion { get; set; }
    public string? Reason { get; set; }
    public string DecidedBy { get; set; } = string.Empty;
    public DateTime DecidedAt { get; set; }
}
