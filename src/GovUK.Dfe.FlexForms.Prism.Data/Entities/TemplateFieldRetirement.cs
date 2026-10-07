namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// A field the template author retired in a template version (its <c>retiredFields</c> entry), with the fields
/// that replace it. Shown as <c>replaced_by</c> on the Removed rows of <c>v_template_field_changes</c>.
/// </summary>
public class TemplateFieldRetirement
{
    public Guid TenantId { get; set; }
    public Guid TemplateVersionId { get; set; }

    /// <summary>The collection field that contained the retired field, or empty for top-level fields.</summary>
    public string ParentFieldId { get; set; } = string.Empty;
    public string FieldId { get; set; } = string.Empty;
    public Guid TemplateId { get; set; }

    /// <summary>The replacing field ids, comma separated, or null when nothing replaces the field.</summary>
    public string? ReplacedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
