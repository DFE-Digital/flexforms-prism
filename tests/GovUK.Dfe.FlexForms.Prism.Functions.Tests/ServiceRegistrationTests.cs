using GovUK.Dfe.FlexForms.Prism.Functions.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

public class ServiceRegistrationTests
{
    private const string ApplicationInsights = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.in.applicationinsights.azure.com/";

    private static ServiceProvider Build(string? applicationInsights = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Prism"] = "Server=localhost;Database=prism;Integrated Security=true",
                ["ServiceBus"] = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==",
                ["ExternalApplicationsApiClient:BaseUrl"] = "https://api.example/",
                ["Prism:Admin:Authority"] = "https://login.microsoftonline.com/tenant/v2.0",
                ["Prism:Admin:Audience"] = "api://prism",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = applicationInsights,
            })
            .Build();

        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddPrismFunctions(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task Every_function_can_be_constructed_from_the_registrations()
    {
        await using var provider = Build(ApplicationInsights);
        await using var scope = provider.CreateAsyncScope();

        Type[] functions =
        [
            typeof(ProjectionFunction), typeof(AdminApi), typeof(ExportPolicyApi), typeof(OperationWorkerFunction),
            typeof(ReconciliationFunction), typeof(GenerationCleanupFunction),
        ];

        foreach (var function in functions)
        {
            Assert.NotNull(ActivatorUtilities.CreateInstance(scope.ServiceProvider, function));
        }
    }

    [Fact]
    public async Task Metrics_are_exported_only_when_Application_Insights_is_configured()
    {
        await using var withInsights = Build(ApplicationInsights);
        await using var withoutInsights = Build();

        Assert.NotNull(withInsights.GetService<MeterProvider>());
        Assert.Null(withoutInsights.GetService<MeterProvider>());
    }

    [Fact]
    public void A_missing_connection_string_fails_at_startup()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPrismFunctions(configuration));
    }
}
