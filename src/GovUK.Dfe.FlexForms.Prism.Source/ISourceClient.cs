using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;

namespace GovUK.Dfe.FlexForms.Prism.Source;

/// <summary>
/// Read-only access to FlexForms source state through the internal Prism API. Every call is made for one
/// tenant, transient failures are retried, and failures surface as <see cref="SourceException"/>.
/// </summary>
public interface ISourceClient
{
    Task<IReadOnlyList<PrismTenantDto>> GetTenantsAsync(CancellationToken cancellationToken);

    Task<PrismApplicationStateDto> GetApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken);

    Task<PrismResponseDto> GetResponseAsync(Guid tenantId, Guid responseId, CancellationToken cancellationToken);

    /// <summary>Template versions are immutable, so they are cached for the lifetime of the process.</summary>
    Task<PrismTemplateVersionDto> GetTemplateVersionAsync(Guid tenantId, Guid templateVersionId, CancellationToken cancellationToken);

    Task<PrismApplicationPageDto> ListApplicationsAsync(
        Guid tenantId,
        DateTime? modifiedSince,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
