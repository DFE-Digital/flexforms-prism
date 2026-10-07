using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Projector;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>Duplicates, reordering and coalescing on the same session.</summary>
public sealed class OrderingScenarios(SqlServerFixture sql) : ScenarioBase(sql)
{
    [Fact]
    public async Task Duplicate_saved_messages_produce_one_generation()
    {
        var app = Guid.NewGuid();
        var saved = Source.Save(app, Named("Ada"));

        var first = await Prism.DeliverAsync(saved);
        var second = await Prism.DeliverAsync(saved);
        var third = await Prism.DeliverAsync(saved);

        Assert.Equal(ProjectionReasons.NewGeneration, first.Reason);
        Assert.Equal(ProjectionReasons.AlreadyProjected, second.Reason);
        Assert.Equal(ProjectionReasons.AlreadyProjected, third.Reason);
        Assert.Single(await Prism.GenerationsAsync(app));
        Assert.Equal("Ada", NameIn(await Prism.CurrentFactsAsync(app)));
    }

    [Fact]
    public async Task Revision_9_arriving_after_revision_10_is_ignored()
    {
        var app = Guid.NewGuid();
        var events = Enumerable.Range(1, 10).Select(i => Source.Save(app, Named($"v{i}"))).ToList();

        var tenth = await Prism.DeliverAsync(events[9]);
        var ninth = await Prism.DeliverAsync(events[8]);

        Assert.Equal(ProjectionStatus.Projected, tenth.Status);
        Assert.Equal(ProjectionReasons.AlreadyProjected, ninth.Reason);
        Assert.Equal(10, (await Prism.StateAsync(app))!.SourceRevision);
        Assert.Equal("v10", NameIn(await Prism.CurrentFactsAsync(app)));
        Assert.Single(await Prism.GenerationsAsync(app));
    }

    [Fact]
    public async Task Queued_revisions_10_and_11_are_projected_once_at_11()
    {
        var app = Guid.NewGuid();
        var events = Enumerable.Range(1, 11).Select(i => Source.Save(app, Named($"v{i}"))).ToList();

        var tenth = await Prism.DeliverAsync(events[9]);
        var eleventh = await Prism.DeliverAsync(events[10]);

        Assert.Equal(ProjectionReasons.NewGeneration, tenth.Reason);
        Assert.Equal(ProjectionReasons.AlreadyProjected, eleventh.Reason);
        var generation = Assert.Single(await Prism.GenerationsAsync(app));
        Assert.Equal(11, generation.SourceRevision);
        Assert.Equal("v11", NameIn(await Prism.CurrentFactsAsync(app)));
    }

    [Fact]
    public async Task Unchanged_answers_advance_the_revision_without_a_new_generation()
    {
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        var outcome = await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        Assert.Equal(ProjectionReasons.HashReused, outcome.Reason);
        Assert.Single(await Prism.GenerationsAsync(app));
        Assert.Equal(2, (await Prism.StateAsync(app))!.SourceRevision);
    }

    [Fact]
    public async Task Submission_is_frozen_while_current_follows_the_application()
    {
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Draft")));
        await Prism.DeliverAsync(Source.Save(app, Named("Final")));
        var submitted = Source.Submit(app);

        var outcome = await Prism.DeliverAsync(submitted);
        var duplicate = await Prism.DeliverAsync(submitted);

        Assert.Equal(ProjectionReasons.NewGeneration, outcome.Reason);
        Assert.Equal(ProjectionStatus.Skipped, duplicate.Status);
        Assert.Equal("Final", NameIn(await Prism.SubmissionFactsAsync(submitted.SubmissionId!.Value)));
        var state = (await Prism.StateAsync(app))!;
        Assert.Equal(ApplicationLifecycle.Submitted, state.Lifecycle);
        Assert.Equal(submitted.SourceRevision, state.SourceRevision);
    }
}
