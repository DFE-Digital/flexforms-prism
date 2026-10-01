using System.Collections.Concurrent;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.FlexForms.Api.Client.Contracts;
using Polly;

namespace GovUK.Dfe.FlexForms.Prism.Source;

public sealed class SourceClient(IInternalPrismClient client, SourceResilience resilience, TemplateVersionCache templates) : ISourceClient
{
    public async Task<IReadOnlyList<PrismTenantDto>> GetTenantsAsync(CancellationToken cancellationToken)
        => await CallAsync(null, "tenants", async ct => (IReadOnlyList<PrismTenantDto>)[.. await client.GetPrismTenantsAsync(ct)], cancellationToken);

    public Task<PrismApplicationStateDto> GetApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
        => CallAsync(tenantId, $"application {applicationId}", ct => client.GetPrismApplicationStateAsync(applicationId, ct), cancellationToken);

    public Task<PrismResponseDto> GetResponseAsync(Guid tenantId, Guid responseId, CancellationToken cancellationToken)
        => CallAsync(tenantId, $"response {responseId}", ct => client.GetPrismResponseAsync(responseId, ct), cancellationToken);

    public Task<PrismTemplateVersionDto> GetTemplateVersionAsync(Guid tenantId, Guid templateVersionId, CancellationToken cancellationToken)
        => templates.GetOrAddAsync(
            tenantId,
            templateVersionId,
            ct => CallAsync(tenantId, $"template version {templateVersionId}", c => client.GetPrismTemplateVersionAsync(templateVersionId, c), ct),
            cancellationToken);

    public Task<PrismApplicationPageDto> ListApplicationsAsync(Guid tenantId, DateTime? modifiedSince, int page, int pageSize, CancellationToken cancellationToken)
        => CallAsync(tenantId, $"applications page {page}", ct => client.ListPrismApplicationsAsync(modifiedSince, page, pageSize, ct), cancellationToken);

    private Task<T> CallAsync<T>(Guid? tenantId, string what, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        => resilience.Pipeline.ExecuteAsync(
            async ct =>
            {
                using var scope = TenantScope.Begin(tenantId);
                try
                {
                    return await call(ct);
                }
                catch (Exception ex) when (SourceErrors.Translate(ex, what, ct) is { } translated)
                {
                    throw translated;
                }
            },
            cancellationToken).AsTask();
}

/// <summary>Retries transient source failures with exponential backoff and jitter.</summary>
public sealed class SourceResilience
{
    public SourceResilience(int maxRetryAttempts = 3, TimeSpan? baseDelay = null)
    {
        Pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetryAttempts,
                Delay = baseDelay ?? TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<SourceException>(ex => ex.IsTransient),
            })
            .Build();
    }

    public ResiliencePipeline Pipeline { get; }
}

/// <summary>
/// Caches template versions by (tenant, version id). Failed fetches are not cached, and concurrent requests for
/// the same version share one fetch.
/// </summary>
public sealed class TemplateVersionCache
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Version), Lazy<Task<PrismTemplateVersionDto>>> entries = new();

    public int Count => entries.Count;

    public async Task<PrismTemplateVersionDto> GetOrAddAsync(
        Guid tenantId,
        Guid templateVersionId,
        Func<CancellationToken, Task<PrismTemplateVersionDto>> fetch,
        CancellationToken cancellationToken)
    {
        var key = (tenantId, templateVersionId);
        var entry = entries.GetOrAdd(key, _ => new Lazy<Task<PrismTemplateVersionDto>>(() => FetchAsync(fetch, templateVersionId)));
        try
        {
            return await entry.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (entry.Value.IsFaulted || entry.Value.IsCanceled)
        {
            entries.TryRemove(new KeyValuePair<(Guid, Guid), Lazy<Task<PrismTemplateVersionDto>>>(key, entry));
            throw;
        }
    }

    private static async Task<PrismTemplateVersionDto> FetchAsync(Func<CancellationToken, Task<PrismTemplateVersionDto>> fetch, Guid templateVersionId)
    {
        // The shared fetch must not be cancelled by whichever caller happened to start it.
        var version = await fetch(CancellationToken.None);
        return version.TemplateVersionId == templateVersionId
            ? version
            : throw new SourceRejectedException($"Asked for template version {templateVersionId} but received {version.TemplateVersionId}.");
    }
}
