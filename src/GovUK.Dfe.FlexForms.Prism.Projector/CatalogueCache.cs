using System.Collections.Concurrent;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;

namespace GovUK.Dfe.FlexForms.Prism.Projector;

/// <summary>
/// Built catalogues, and the catalogue states already written to the database. Template versions are
/// immutable, so both live for the lifetime of the process.
/// </summary>
public sealed class CatalogueCache
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid TemplateVersion), TemplateCatalogue> catalogues = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid TemplateVersion, int ContractVersion, int PolicyVersion), byte> ensured = new();

    public TemplateCatalogue GetOrBuild(Guid tenantId, Guid templateVersionId, string templateJson)
        => catalogues.GetOrAdd((tenantId, templateVersionId), static (_, json) => TemplateCatalogueBuilder.Build(json), templateJson);

    public bool IsEnsured(Guid tenantId, Guid templateVersionId, int contractVersion, int policyVersion)
        => ensured.ContainsKey((tenantId, templateVersionId, contractVersion, policyVersion));

    public void MarkEnsured(Guid tenantId, Guid templateVersionId, int contractVersion, int policyVersion)
        => ensured.TryAdd((tenantId, templateVersionId, contractVersion, policyVersion), 0);
}
