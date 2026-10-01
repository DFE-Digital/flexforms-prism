using GovUK.Dfe.FlexForms.Prism.Functions.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

public class ServiceRegistrationTests
{
    [Fact]
    public async Task Every_function_can_be_constructed_from_the_registrations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Prism"] = "Server=localhost;Database=prism;Integrated Security=true",
                ["ServiceBus"] = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==",
                ["ExternalApplicationsApiClient:BaseUrl"] = "https://api.example/",
                ["Prism:Admin:Authority"] = "https://login.microsoftonline.com/tenant/v2.0",
                ["Prism:Admin:Audience"] = "api://prism",
            })
            .Build();

        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddPrismFunctions(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        Type[] functions =
        [
            typeof(ProjectionFunction), typeof(AdminApi), typeof(OperationWorkerFunction),
            typeof(ReconciliationFunction), typeof(GenerationCleanupFunction),
        ];

        foreach (var function in functions)
        {
            Assert.NotNull(ActivatorUtilities.CreateInstance(scope.ServiceProvider, function));
        }
    }

    [Fact]
    public void A_missing_connection_string_fails_at_startup()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPrismFunctions(configuration));
    }
}
