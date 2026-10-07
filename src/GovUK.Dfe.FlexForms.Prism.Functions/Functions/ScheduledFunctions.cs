using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Maintenance;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Functions;

/// <summary>Advances pending and running backfill and reconciliation operations every minute.</summary>
public sealed class OperationWorkerFunction(OperationProcessor processor)
{
    [Function(nameof(OperationWorkerFunction))]
    public Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
        => processor.RunAsync(cancellationToken);
}

/// <summary>
/// Nightly drift check across every tenant. It records a reconciliation operation, which the operation worker
/// pages through, enqueuing Resync messages only for applications that have drifted.
/// </summary>
public sealed partial class ReconciliationFunction(OperationService operations, ILogger<ReconciliationFunction> logger)
{
    public const string RequestedBy = "system:reconciliation";

    [Function(nameof(ReconciliationFunction))]
    public async Task Run([TimerTrigger("0 0 2 * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (await operations.HasActiveAsync(OperationKind.Reconciliation, cancellationToken))
        {
            LogStillRunning();
            return;
        }

        var operation = await operations.CreateAsync(OperationKind.Reconciliation, null, null, RequestedBy, cancellationToken);
        LogScheduled(operation.OperationId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The previous reconciliation is still running; not starting another")]
    private partial void LogStillRunning();

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation {OperationId} scheduled")]
    private partial void LogScheduled(Guid operationId);
}

/// <summary>Deletes superseded generations once they are past retention and nothing references them.</summary>
public sealed partial class GenerationCleanupFunction(
    IGenerationCleaner cleaner,
    ControlPlaneMetrics metrics,
    TimeProvider clock,
    IOptions<PrismFunctionsOptions> options,
    ILogger<GenerationCleanupFunction> logger)
{
    [Function(nameof(GenerationCleanupFunction))]
    public async Task Run([TimerTrigger("0 30 3 * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var settings = options.Value.Cleanup;
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-settings.RetentionDays);
        var result = await cleaner.DeleteSupersededAsync(cutoff, settings.MaxGenerationsPerRun, cancellationToken);
        metrics.Cleaned(result.GenerationsDeleted, result.FactsDeleted);
        LogCleaned(result.GenerationsDeleted, result.FactsDeleted, cutoff);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Generations} superseded generations and {Facts} facts superseded before {Cutoff:o}")]
    private partial void LogCleaned(int generations, long facts, DateTime cutoff);
}
