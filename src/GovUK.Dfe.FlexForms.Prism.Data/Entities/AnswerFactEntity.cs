using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Data.Entities;

public class AnswerFactEntity
{
    public long Id { get; set; }
    public Guid GenerationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public byte[] LogicalKeyHash { get; set; } = [];
    public string FieldId { get; set; } = string.Empty;
    public string? ParentFieldId { get; set; }
    public string OccurrencePath { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public int? ItemOrdinal { get; set; }
    public string NestedPath { get; set; } = string.Empty;
    public string? DataType { get; set; }
    public bool? IsCompleted { get; set; }
    public InterpretationStatus InterpretationStatus { get; set; }
    public string? ValueString { get; set; }
    public decimal? ValueDecimal { get; set; }
    public bool? ValueBool { get; set; }
    public DateOnly? ValueDate { get; set; }
    public DateTime? ValueDateTime { get; set; }
    public string? ValueJson { get; set; }
    public string? RawValue { get; set; }

    public ProjectionGeneration? Generation { get; set; }
}
