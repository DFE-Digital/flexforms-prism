using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.FlexForms.Api.Client.Contracts;
using GovUK.Dfe.FlexForms.Api.Client.Settings;
using GovUK.Dfe.FlexForms.Prism.Source;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GovUK.Dfe.FlexForms.Prism.Projector.Tests;

public class SourceClientTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid ApplicationId = Guid.NewGuid();

    private readonly IInternalPrismClient api = Substitute.For<IInternalPrismClient>();
    private readonly SourceClient client;

    public SourceClientTests()
    {
        client = new SourceClient(api, new SourceResilience(maxRetryAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1)), new TemplateVersionCache());
    }

    private static ExternalApplicationsException ApiError(int status) => new("failed", status, "", new Dictionary<string, IEnumerable<string>>(), null!);

    private static PrismTemplateVersionDto Template(Guid id) => new(id, Guid.NewGuid(), "1", "{}", DateTime.UtcNow);

    [Fact]
    public async Task Transient_failures_are_retried()
    {
        var state = new PrismApplicationStateDto(ApplicationId, "A", 1, null, false, null, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, null, null, null, null, null, null, null);
        api.GetPrismApplicationStateAsync(ApplicationId, Arg.Any<CancellationToken>())
            .Returns(_ => throw ApiError(503), _ => Task.FromResult(state));

        Assert.Same(state, await client.GetApplicationAsync(Tenant, ApplicationId, default));
        await api.Received(2).GetPrismApplicationStateAsync(ApplicationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exhausted_retries_surface_as_unavailable()
    {
        api.GetPrismApplicationStateAsync(ApplicationId, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("refused"));

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => client.GetApplicationAsync(Tenant, ApplicationId, default));

        Assert.True(ex.IsTransient);
        await api.Received(3).GetPrismApplicationStateAsync(ApplicationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Not_found_is_permanent_and_not_retried()
    {
        api.GetPrismApplicationStateAsync(ApplicationId, Arg.Any<CancellationToken>()).ThrowsAsync(ApiError(404));

        var ex = await Assert.ThrowsAsync<SourceNotFoundException>(() => client.GetApplicationAsync(Tenant, ApplicationId, default));

        Assert.False(ex.IsTransient);
        await api.Received(1).GetPrismApplicationStateAsync(ApplicationId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(422, false)]
    [InlineData(200, false)]
    [InlineData(401, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    public async Task Status_codes_are_classified(int status, bool transient)
    {
        api.GetPrismResponseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(ApiError(status));

        var ex = await Assert.ThrowsAnyAsync<SourceException>(() => client.GetResponseAsync(Tenant, Guid.NewGuid(), default));

        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task Each_call_runs_with_its_tenant_in_scope()
    {
        Guid? seen = null;
        api.GetPrismResponseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            seen = TenantScope.TenantId;
            return new PrismResponseDto(Guid.NewGuid(), ApplicationId, 1, DateTime.UtcNow, "{}");
        });

        await client.GetResponseAsync(Tenant, Guid.NewGuid(), default);

        Assert.Equal(Tenant, seen);
        Assert.Null(TenantScope.TenantId);
    }

    [Fact]
    public void The_settings_provider_sends_the_scoped_tenant_without_token_exchange()
    {
        var provider = new TenantScopedApiClientSettingsProvider(new ApiClientSettings { BaseUrl = "https://api/", RequestTokenExchange = true });

        using (TenantScope.Begin(Tenant))
        {
            var settings = provider.GetSettings();
            Assert.Equal(Tenant, settings.TenantId);
            Assert.False(settings.RequestTokenExchange);
            Assert.Equal("https://api/", settings.BaseUrl);
        }

        Assert.Null(provider.GetSettings().TenantId);
    }

    [Fact]
    public async Task Template_versions_are_cached_per_tenant()
    {
        var id = Guid.NewGuid();
        api.GetPrismTemplateVersionAsync(id, Arg.Any<CancellationToken>()).Returns(Template(id));

        await client.GetTemplateVersionAsync(Tenant, id, default);
        await client.GetTemplateVersionAsync(Tenant, id, default);
        await client.GetTemplateVersionAsync(Guid.NewGuid(), id, default);

        await api.Received(2).GetPrismTemplateVersionAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_template_fetches_are_not_cached()
    {
        var id = Guid.NewGuid();
        api.GetPrismTemplateVersionAsync(id, Arg.Any<CancellationToken>())
            .Returns(_ => throw ApiError(404), _ => Task.FromResult(Template(id)));

        await Assert.ThrowsAsync<SourceNotFoundException>(() => client.GetTemplateVersionAsync(Tenant, id, default));
        Assert.Equal(id, (await client.GetTemplateVersionAsync(Tenant, id, default)).TemplateVersionId);
    }

    [Fact]
    public async Task A_template_with_the_wrong_id_is_rejected()
    {
        var id = Guid.NewGuid();
        api.GetPrismTemplateVersionAsync(id, Arg.Any<CancellationToken>()).Returns(Template(Guid.NewGuid()));

        await Assert.ThrowsAsync<SourceRejectedException>(() => client.GetTemplateVersionAsync(Tenant, id, default));
    }
}
