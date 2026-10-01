namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

/// <summary>
/// One materialised projection of an application. Facts belong to exactly one generation and are only
/// visible through the views once the generation is referenced by a state row or a submission snapshot.
/// </summary>
public class ProjectionGeneration
{
    public Guid GenerationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public GenerationKind Kind { get; set; }
    public GenerationStatus Status { get; set; }
    public long SourceRevision { get; set; }
    public Guid ResponseId { get; set; }
    public long? ResponseRevision { get; set; }
    public Guid? SubmissionId { get; set; }
    public Guid TemplateId { get; set; }
    public Guid TemplateVersionId { get; set; }
    public byte[] SourceHash { get; set; } = [];
    public int ProjectorVersion { get; set; }
    public int ContractVersion { get; set; }
    public int ExportPolicyVersion { get; set; }
    public int FactCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? SupersededAt { get; set; }

    public ICollection<AnswerFactEntity> Facts { get; set; } = [];
}
