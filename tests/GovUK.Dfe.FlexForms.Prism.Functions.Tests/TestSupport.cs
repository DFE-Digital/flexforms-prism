using System.Diagnostics.Metrics;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

internal static class TestSupport
{
    public static ApplicationProjectionRequestedEvent Event(
        ProjectionReason reason = ProjectionReason.Saved,
        long revision = 3,
        Guid? operationId = null) =>
        new(ApplicationProjectionRequestedEvent.CurrentContractVersion, Guid.NewGuid(), Guid.NewGuid(), reason, revision,
            Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), operationId, new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
}

internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, this);
        meters.Add(meter);
        return meter;
    }

    public void Dispose() => meters.ForEach(m => m.Dispose());
}
