using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Projector;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>Crashes, redelivery and concurrent writers.</summary>
public sealed class FailureScenarios(SqlServerFixture sql, ITestOutputHelper output) : ScenarioBase(sql)
{
    [Fact]
    public async Task Crash_after_bulk_copy_rolls_back_and_keeps_the_previous_generation_visible()
    {
        var app = Guid.NewGuid();
        await Prism.InstallCrashTriggerAsync();
        await Prism.DeliverAsync(Source.Save(app, Named("Before")));
        var before = Assert.Single(await Prism.GenerationsAsync(app));
        var saved = Source.Save(app, Named("After"));

        await Prism.ExecuteSqlAsync($"INSERT INTO scenario.crash_points (application_id) VALUES ({app})");
        try
        {
            var crash = await Assert.ThrowsAsync<SqlException>(() => Prism.DeliverAsync(saved));
            Assert.Contains("Simulated crash", crash.Message);
        }
        finally
        {
            await Prism.ExecuteSqlAsync($"DELETE FROM scenario.crash_points WHERE application_id = {app}");
        }

        Assert.Equal("Before", NameIn(await Prism.CurrentFactsAsync(app)));
        var survivor = Assert.Single(await Prism.GenerationsAsync(app));
        Assert.Equal(before.GenerationId, survivor.GenerationId);
        Assert.Equal(GenerationStatus.Active, survivor.Status);
        Assert.Equal(before.FactCount, await Prism.FactCountAsync(before.GenerationId));

        var retried = await Prism.DeliverAsync(saved);

        Assert.Equal(ProjectionReasons.NewGeneration, retried.Reason);
        Assert.Equal("After", NameIn(await Prism.CurrentFactsAsync(app)));
    }

    [Fact]
    public async Task Redelivery_after_commit_is_skipped()
    {
        var app = Guid.NewGuid();
        var saved = Source.Save(app, Named("Ada"));
        var committed = await Prism.DeliverAsync(saved);

        // The lock was lost before the message was completed, so Service Bus hands it out again.
        var redelivered = await Prism.DeliverAsync(saved);

        Assert.Equal(ProjectionReasons.AlreadyProjected, redelivered.Reason);
        var generation = Assert.Single(await Prism.GenerationsAsync(app));
        Assert.Equal(committed.GenerationId, generation.GenerationId);
    }

    [Fact]
    public async Task Concurrent_writers_reading_different_revisions_converge_on_the_highest()
    {
        var app = Guid.NewGuid();
        var events = Enumerable.Range(1, 12).Select(i => Source.Save(app, Named($"v{i}"))).ToList();

        var random = new Random(7);
        long highestSeen = 0;
        Source.ApplicationView = state =>
        {
            var revision = random.Next(1, (int)state.SourceRevision + 1);
            highestSeen = Math.Max(highestSeen, revision);
            return Source.Snapshot(app, revision);
        };

        var deliveries = Enumerable.Range(0, 24)
            .Select(i => i % 3 == 0 ? Resync(events[^1]) : events[i % events.Count])
            .Select(e => Task.Run(() => DeliverWithRetriesAsync(e)))
            .ToList();
        var outcomes = await Task.WhenAll(deliveries);
        output.WriteLine(string.Join(", ", outcomes.GroupBy(o => o.Reason).Select(g => $"{g.Key}={g.Count()}")));

        var afterRace = (await Prism.StateAsync(app))!;
        Assert.Equal(highestSeen, afterRace.SourceRevision);
        Assert.Equal($"v{highestSeen}", NameIn(await Prism.CurrentFactsAsync(app)));
        await AssertOneActiveGenerationAsync(app);

        Source.ApplicationView = null;
        await Prism.DeliverAsync(Resync(events[^1]));

        Assert.Equal(12, (await Prism.StateAsync(app))!.SourceRevision);
        Assert.Equal("v12", NameIn(await Prism.CurrentFactsAsync(app)));
        await AssertOneActiveGenerationAsync(app);
    }

    [Fact]
    public async Task Stale_backfill_message_after_live_processing_changes_nothing()
    {
        var app = Guid.NewGuid();
        var early = Source.Save(app, Named("v1"));
        var enqueuedEarly = Resync(early);
        await Prism.DeliverAsync(Source.Save(app, Named("v2")));
        await Prism.DeliverAsync(Source.Save(app, Named("v3")));
        var generationsBefore = await Prism.GenerationsAsync(app);

        var stale = await Prism.DeliverAsync(enqueuedEarly);
        var (backfill, enqueued) = await Prism.RunOperationAsync(OperationKind.Backfill);
        var (reconciliation, drifted) = await Prism.RunOperationAsync(OperationKind.Reconciliation);

        Assert.Equal(ProjectionReasons.AlreadyProjected, stale.Reason);
        Assert.Equal(BackfillStatus.Completed, backfill.Status);
        Assert.Contains(enqueued, e => e.ApplicationId == app);
        Assert.Equal(BackfillStatus.Completed, reconciliation.Status);
        Assert.DoesNotContain(drifted, e => e.ApplicationId == app);
        Assert.Equal(generationsBefore.Count, (await Prism.GenerationsAsync(app)).Count);
        Assert.Equal("v3", NameIn(await Prism.CurrentFactsAsync(app)));
    }

    private static ApplicationProjectionRequestedEvent Resync(ApplicationProjectionRequestedEvent e) => e with { Reason = ProjectionReason.Resync, ResponseId = null, OperationId = Guid.NewGuid() };

    /// <summary>Retries transient SQL failures the way Service Bus redelivery would.</summary>
    private async Task<ProjectionOutcome> DeliverWithRetriesAsync(ApplicationProjectionRequestedEvent message)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Prism.DeliverAsync(message);
            }
            catch (SqlException ex) when (attempt < 5)
            {
                output.WriteLine($"Attempt {attempt} failed with SQL error {ex.Number}; retrying.");
            }
        }
    }

    private async Task AssertOneActiveGenerationAsync(Guid app)
    {
        var generations = await Prism.GenerationsAsync(app);
        Assert.Single(generations, g => g.Status == GenerationStatus.Active);
        Assert.DoesNotContain(generations, g => g.Status == GenerationStatus.Building);
        Assert.Equal((await Prism.StateAsync(app))!.ActiveGenerationId, generations.Single(g => g.Status == GenerationStatus.Active).GenerationId);
    }
}
