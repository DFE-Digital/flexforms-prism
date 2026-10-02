using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;

namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// What happens to fields without an explicit decision. A row with <see cref="TemplateId"/> equal to
/// <see cref="Guid.Empty"/> is the tenant-wide default; a row for a template overrides it. With no row at all
/// the default is <see cref="DefaultExportMode.ApproveFirst"/>.
/// </summary>
public class ExportDefault
{
    public Guid TenantId { get; set; }

    /// <summary>The template this default applies to, or <see cref="Guid.Empty"/> for the whole tenant.</summary>
    public Guid TemplateId { get; set; }

    /// <summary>Null means inherit: from the tenant for a template row, or the built-in default for the tenant row.</summary>
    public DefaultExportMode? Mode { get; set; }

    public int PolicyVersion { get; set; }
    public string? Reason { get; set; }
    public string DecidedBy { get; set; } = string.Empty;
    public DateTime DecidedAt { get; set; }
}
