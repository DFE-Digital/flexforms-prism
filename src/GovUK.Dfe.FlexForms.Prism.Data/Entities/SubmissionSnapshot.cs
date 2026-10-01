namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// One row per submission. The selected generation holds the facts of the exact submitted response.
/// </summary>
public class SubmissionSnapshot
{
    public Guid TenantId { get; set; }
    public Guid SubmissionId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid ResponseId { get; set; }
    public long? ResponseRevision { get; set; }
    public long SourceRevision { get; set; }
    public DateTime SubmittedAt { get; set; }
    public Guid TemplateId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public Guid SelectedGenerationId { get; set; }
    public byte[] SourceHash { get; set; } = [];
    public int ProjectorVersion { get; set; }
    public int ContractVersion { get; set; }
    public int ExportPolicyVersion { get; set; }
    public DateTime ProjectedAt { get; set; }

    public ProjectionGeneration? SelectedGeneration { get; set; }
}
