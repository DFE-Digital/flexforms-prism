namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// Marks an application as deleted at the source. A tombstoned application is never projected again.
/// </summary>
public class DeletionTombstone
{
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public long SourceRevision { get; set; }
    public DateTime DeletedAt { get; set; }
    public DateTime RecordedAt { get; set; }
}
