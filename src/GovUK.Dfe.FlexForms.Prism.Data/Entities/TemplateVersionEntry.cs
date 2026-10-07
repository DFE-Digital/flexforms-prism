namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// A template version Prism has catalogued. <see cref="CreatedOn"/> is the source creation time, which orders the
/// versions of a template for <c>v_template_field_changes</c>.
/// </summary>
public class TemplateVersionEntry
{
    public Guid TenantId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public Guid TemplateId { get; set; }
    public string? VersionNumber { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime CataloguedAt { get; set; }
}
