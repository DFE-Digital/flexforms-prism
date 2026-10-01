namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// The latest projected state of an application, guarded by a version-aware compare-and-swap on
/// (source revision, projector version, contract version, export policy version).
/// </summary>
public class ApplicationProjectionState
{
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? ActiveGenerationId { get; set; }
    public Guid? ResponseId { get; set; }
    public long SourceRevision { get; set; }
    public ApplicationLifecycle Lifecycle { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? TemplateVersionId { get; set; }
    public byte[]? SourceHash { get; set; }
    public int ProjectorVersion { get; set; }
    public int ContractVersion { get; set; }
    public int ExportPolicyVersion { get; set; }
    public DateTime? SourceOccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ProjectedAt { get; set; }

    public ProjectionGeneration? ActiveGeneration { get; set; }
}
