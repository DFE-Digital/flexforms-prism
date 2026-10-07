using GovUK.Dfe.FlexForms.Prism.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

// The worker never reads host.json. appsettings.json holds shipped defaults; inserted first so app settings override it.
builder.Configuration.Sources.Insert(0, new JsonConfigurationSource
{
    Path = Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    Optional = true,
});

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// The Application Insights provider only passes warnings by default; let host.json and app settings decide.
builder.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

// host.json log levels only apply to the host process, not this worker. App settings (Logging__LogLevel__...) override these.
builder.Logging
    .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
    .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning)
    .AddFilter("Azure.Core", LogLevel.Error)
    .AddConfiguration(builder.Configuration.GetSection("Logging"));

builder.Services.AddPrismFunctions(builder.Configuration);

builder.Build().Run();
