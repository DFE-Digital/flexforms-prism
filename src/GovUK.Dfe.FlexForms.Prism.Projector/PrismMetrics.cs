using System.Diagnostics;
using System.Diagnostics.Metrics;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;

namespace GovUK.Dfe.FlexForms.Prism.Projector;

public sealed class PrismMetrics
{
    public const string MeterName = "GovUK.Dfe.FlexForms.Prism";

    private readonly Histogram<double> lag;
    private readonly Counter<long> succeeded;
    private readonly Counter<long> failed;
    private readonly Counter<long> retried;
    private readonly Counter<long> skipped;
    private readonly Counter<long> casConflicts;
    private readonly Counter<long> hashReuses;
    private readonly Counter<long> generationsCreated;
    private readonly Counter<long> generationsSuperseded;
    private readonly Histogram<double> sourceLatency;
    private readonly Histogram<double> flattenDuration;
    private readonly Histogram<double> writeDuration;

    public PrismMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        lag = meter.CreateHistogram<double>("prism.projection.lag", "s", "Time from the source transition to the projection completing.");
        succeeded = meter.CreateCounter<long>("prism.projection.succeeded", description: "Messages handled without error, including skips.");
        failed = meter.CreateCounter<long>("prism.projection.failed", description: "Messages that failed, by failure reason.");
        retried = meter.CreateCounter<long>("prism.projection.retried", description: "Messages delivered more than once.");
        skipped = meter.CreateCounter<long>("prism.projection.skipped", description: "Messages that needed no write, by skip reason.");
        casConflicts = meter.CreateCounter<long>("prism.projection.cas_conflicts", description: "Writes lost to a concurrent newer projection.");
        hashReuses = meter.CreateCounter<long>("prism.projection.hash_reuses", description: "Saves whose facts matched the active generation.");
        generationsCreated = meter.CreateCounter<long>("prism.generations.created", description: "Generations written, by kind.");
        generationsSuperseded = meter.CreateCounter<long>("prism.generations.superseded", description: "Generations replaced by a newer one, by kind.");
        sourceLatency = meter.CreateHistogram<double>("prism.source.duration", "ms", "FlexForms API call duration, by operation.");
        flattenDuration = meter.CreateHistogram<double>("prism.flatten.duration", "ms", "Time to parse, flatten and hash a response.");
        writeDuration = meter.CreateHistogram<double>("prism.write.duration", "ms", "Projection write duration (bulk copy and SQL), by operation.");
    }

    public void Succeeded(ProjectionReason reason, ProjectionOutcome outcome, DateTime occurredAt, DateTime now)
    {
        var reasonTag = new KeyValuePair<string, object?>("reason", reason.ToString());
        succeeded.Add(1, reasonTag, new("status", outcome.Status.ToString()));
        lag.Record(Math.Max(0, (now - occurredAt).TotalSeconds), reasonTag);
    }

    public void Failed(ProjectionReason reason, string failure, bool permanent) =>
        failed.Add(1, new("reason", reason.ToString()), new("failure", failure), new("permanent", permanent));

    public void Retried(ProjectionReason reason) => retried.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    public void Skipped(string skipReason)
    {
        skipped.Add(1, new KeyValuePair<string, object?>("skip_reason", skipReason));
        if (skipReason == ProjectionReasons.CasConflict)
        {
            casConflicts.Add(1);
        }
    }

    public void HashReused() => hashReuses.Add(1);

    public void GenerationCreated(string kind, bool supersededPrevious)
    {
        var kindTag = new KeyValuePair<string, object?>("kind", kind);
        generationsCreated.Add(1, kindTag);
        if (supersededPrevious)
        {
            generationsSuperseded.Add(1, kindTag);
        }
    }

    public void SourceCall(string operation, TimeSpan elapsed) =>
        sourceLatency.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));

    public void Flattened(TimeSpan elapsed) => flattenDuration.Record(elapsed.TotalMilliseconds);

    public void Written(string operation, TimeSpan elapsed) =>
        writeDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));

    public static async Task<T> TimeAsync<T>(Func<Task<T>> work, Action<TimeSpan> record)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await work();
        }
        finally
        {
            record(Stopwatch.GetElapsedTime(started));
        }
    }
}
