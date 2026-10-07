namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// An audited control-plane request to enqueue Resync messages for a tenant or for every tenant. A backfill
/// enqueues every application; a reconciliation only those whose Prism state has drifted from the source.
/// </summary>
public class BackfillOperation
{
    public Guid OperationId { get; set; }
    public OperationKind Kind { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime? ModifiedSince { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public BackfillStatus Status { get; set; }

    /// <summary>The tenant being paged when the worker last stopped. Tenants are processed in id order.</summary>
    public Guid? CurrentTenantId { get; set; }

    /// <summary>The next source page to read for <see cref="CurrentTenantId"/>.</summary>
    public int NextPage { get; set; } = 1;

    public int PagesProcessed { get; set; }
    public int ApplicationsScanned { get; set; }
    public int MessagesEnqueued { get; set; }
    public string? CancelledBy { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
