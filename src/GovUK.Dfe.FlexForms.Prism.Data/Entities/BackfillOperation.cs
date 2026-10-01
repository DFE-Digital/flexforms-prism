namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// An audited control-plane request to enqueue Resync messages for a tenant or for every tenant.
/// </summary>
public class BackfillOperation
{
    public Guid OperationId { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime? ModifiedSince { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public BackfillStatus Status { get; set; }
    public int PagesProcessed { get; set; }
    public int MessagesEnqueued { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
