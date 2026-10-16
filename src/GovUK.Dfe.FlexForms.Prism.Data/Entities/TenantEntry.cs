namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// A FlexForms tenant Prism has seen, with the display name from the internal tenants API.
/// </summary>
public class TenantEntry
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}
