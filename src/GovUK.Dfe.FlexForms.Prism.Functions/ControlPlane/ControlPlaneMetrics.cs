using System.Diagnostics.Metrics;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Projector;

namespace GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

public sealed class ControlPlaneMetrics
{
    private readonly Counter<long> enqueued;
    private readonly Counter<long> drift;
    private readonly Counter<long> operationsFinished;
    private readonly Counter<long> generationsDeleted;
    private readonly Counter<long> factsDeleted;

    public ControlPlaneMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(PrismMetrics.MeterName);
        enqueued = meter.CreateCounter<long>("prism.operations.enqueued", description: "Resync messages enqueued, by operation kind.");
        drift = meter.CreateCounter<long>("prism.reconciliation.drift", description: "Applications whose Prism state drifted from the source, by drift kind.");
        operationsFinished = meter.CreateCounter<long>("prism.operations.finished", description: "Backfill and reconciliation operations finished, by kind and status.");
        generationsDeleted = meter.CreateCounter<long>("prism.cleanup.generations_deleted", description: "Superseded generations deleted after retention.");
        factsDeleted = meter.CreateCounter<long>("prism.cleanup.facts_deleted", description: "Facts deleted with superseded generations.");
    }

    public void Enqueued(OperationKind kind, int count) => enqueued.Add(count, new KeyValuePair<string, object?>("kind", kind.ToString()));

    public void Drift(string driftKind) => drift.Add(1, new KeyValuePair<string, object?>("drift_kind", driftKind));

    public void Finished(OperationKind kind, BackfillStatus status) =>
        operationsFinished.Add(1, new("kind", kind.ToString()), new("status", status.ToString()));

    public void Cleaned(int generations, long facts)
    {
        generationsDeleted.Add(generations);
        factsDeleted.Add(facts);
    }
}
