using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Projector;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>Deleted applications stay deleted, however late or stale the other messages are.</summary>
public sealed class DeletionScenarios(SqlServerFixture sql) : ScenarioBase(sql)
{
    [Fact]
    public async Task Stale_save_after_a_delete_does_not_resurrect_the_application()
    {
        var app = Guid.NewGuid();
        var saved = Source.Save(app, Named("Ada"));
        await Prism.DeliverAsync(saved);
        var deleted = await Prism.DeliverAsync(Source.Delete(app));

        var lateSave = await Prism.DeliverAsync(saved);
        Source.ApplicationView = _ => Source.Snapshot(app, saved.SourceRevision);
        var staleResync = await Prism.DeliverAsync(saved with { Reason = ProjectionReason.Resync, OperationId = Guid.NewGuid(), SourceRevision = 99 });

        Assert.Equal(ProjectionReasons.Deleted, deleted.Reason);
        Assert.Equal(ProjectionReasons.Tombstoned, lateSave.Reason);
        Assert.Equal(ProjectionReasons.Tombstoned, staleResync.Reason);
        Assert.Empty(await Prism.CurrentFactsAsync(app));
        Assert.Equal(ApplicationLifecycle.Deleted, (await Prism.StateAsync(app))!.Lifecycle);
    }

    [Fact]
    public async Task Delete_overtaking_the_save_leaves_nothing_visible()
    {
        var app = Guid.NewGuid();
        var saved = Source.Save(app, Named("Ada"));
        var delete = Source.Delete(app);

        await Prism.DeliverAsync(delete);
        var late = await Prism.DeliverAsync(saved);

        Assert.Equal(ProjectionReasons.Tombstoned, late.Reason);
        Assert.Empty(await Prism.GenerationsAsync(app));
        Assert.Empty(await Prism.CurrentFactsAsync(app));
    }

    [Fact]
    public async Task Lost_delete_is_repaired_by_reconciliation()
    {
        var app = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));
        await Prism.DeliverAsync(Source.Save(untouched, Named("Bob")));
        Source.Delete(app);

        var (operation, enqueued) = await Prism.RunOperationAsync(OperationKind.Reconciliation);

        var repair = Assert.Single(enqueued);
        Assert.Equal(app, repair.ApplicationId);
        Assert.Equal(1, operation.MessagesEnqueued);
        Assert.Empty(await Prism.CurrentFactsAsync(app));
        Assert.Equal(ApplicationLifecycle.Deleted, (await Prism.StateAsync(app))!.Lifecycle);
        Assert.Equal("Bob", NameIn(await Prism.CurrentFactsAsync(untouched)));
    }
}
