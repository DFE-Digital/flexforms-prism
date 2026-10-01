using GovUK.Dfe.FlexForms.Api.Client.Settings;

namespace GovUK.Dfe.FlexForms.Prism.Source;

/// <summary>
/// The tenant that API calls in the current async flow are made for. The API client reads it through
/// <see cref="TenantScopedApiClientSettingsProvider"/> and sends it as the <c>X-Tenant-ID</c> header.
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public static Guid? TenantId => Current.Value;

    /// <summary>Sets the tenant until the returned scope is disposed. Pass null for tenant-less calls.</summary>
    public static IDisposable Begin(Guid? tenantId)
    {
        var previous = Current.Value;
        Current.Value = tenantId;
        return new Scope(previous);
    }

    private sealed class Scope(Guid? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                Current.Value = previous;
                disposed = true;
            }
        }
    }
}

/// <summary>
/// API client settings from configuration, with the tenant taken from <see cref="TenantScope"/> instead of a
/// fixed value, so one client can serve every tenant.
/// </summary>
public sealed class TenantScopedApiClientSettingsProvider(ApiClientSettings settings) : IApiClientSettingsProvider
{
    public ApiClientSettings GetSettings() => new()
    {
        BaseUrl = settings.BaseUrl,
        ClientId = settings.ClientId,
        ClientSecret = settings.ClientSecret,
        Authority = settings.Authority,
        Scope = settings.Scope,
        RequestTokenExchange = false,
        AutoRegisterUsers = false,
        HeadersToForward = settings.HeadersToForward,
        TenantId = TenantScope.TenantId,
    };
}
