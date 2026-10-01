using System.Security.Cryptography;
using System.Text;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Facts;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

internal static class TestData
{
    public static readonly ProjectionVersions V1 = new(1, 1, 1);

    public static AnswerFact Fact(string fieldId, string value, string occurrencePath = "") => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{fieldId}|{occurrencePath}|")),
        fieldId,
        ParentFieldId: null,
        occurrencePath,
        ItemId: null,
        ItemOrdinal: null,
        NestedPath: string.Empty,
        DataType: "text",
        IsCompleted: true,
        InterpretationStatus.Ok,
        ValueString: value,
        ValueDecimal: null,
        ValueBool: null,
        ValueDate: null,
        ValueDateTime: null,
        ValueJson: null,
        RawValue: value);

    public static byte[] Hash(string seed) => SHA256.HashData(Encoding.UTF8.GetBytes(seed));

    public static CurrentProjection Current(
        Guid tenantId,
        Guid applicationId,
        long revision,
        ProjectionVersions? versions = null,
        ApplicationLifecycle lifecycle = ApplicationLifecycle.Draft,
        string hashSeed = "h") => new(
        tenantId,
        applicationId,
        revision,
        ResponseId: Guid.NewGuid(),
        ResponseRevision: revision,
        TemplateId: Guid.NewGuid(),
        TemplateVersionId: Guid.NewGuid(),
        lifecycle,
        Hash(hashSeed),
        versions ?? V1,
        SourceOccurredAt: DateTime.UtcNow);

    public static SubmissionProjection Submission(Guid tenantId, Guid applicationId, Guid submissionId, long revision, ProjectionVersions? versions = null) => new(
        tenantId,
        applicationId,
        submissionId,
        revision,
        ResponseId: Guid.NewGuid(),
        ResponseRevision: revision - 1,
        SubmittedAt: DateTime.UtcNow,
        TemplateId: Guid.NewGuid(),
        TemplateVersionId: Guid.NewGuid(),
        Hash("s"),
        versions ?? V1);
}
