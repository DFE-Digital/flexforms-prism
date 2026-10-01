using System.Diagnostics;
using System.Text.Json;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Projector;
using Xunit.Abstractions;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

public sealed class CleanupAndLoadScenarios(SqlServerFixture sql, ITestOutputHelper output) : ScenarioBase(sql)
{
    [Fact]
    public async Task Cleanup_never_deletes_a_generation_that_is_still_referenced()
    {
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Draft")));
        await Prism.DeliverAsync(Source.Save(app, Named("Final")));
        var submitted = Source.Submit(app);
        await Prism.DeliverAsync(submitted);
        var before = await Prism.GenerationsAsync(app);
        var currentBefore = await Prism.CurrentFactsAsync(app);
        var submissionBefore = await Prism.SubmissionFactsAsync(submitted.SubmissionId!.Value);

        await Prism.CleanupAsync(supersededBefore: DateTime.UtcNow.AddDays(1));

        var after = await Prism.GenerationsAsync(app);
        var superseded = Assert.Single(before, g => g.Status == GenerationStatus.Superseded);
        Assert.DoesNotContain(after, g => g.GenerationId == superseded.GenerationId);
        Assert.Equal(0, await Prism.FactCountAsync(superseded.GenerationId));
        Assert.Contains(after, g => g.Kind == GenerationKind.Current && g.Status == GenerationStatus.Active);
        Assert.Contains(after, g => g.Kind == GenerationKind.Submission);
        Assert.Equal(currentBefore, await Prism.CurrentFactsAsync(app));
        Assert.Equal(submissionBefore, await Prism.SubmissionFactsAsync(submitted.SubmissionId!.Value));
        Assert.Equal("Final", NameIn(submissionBefore));
    }

    [Fact]
    public async Task Large_application_is_projected_completely_and_reused_when_unchanged()
    {
        const int Members = 2_500;
        var app = Guid.NewGuid();
        var members = JsonSerializer.Serialize(Enumerable.Range(1, Members).Select(i => new Dictionary<string, string>
        {
            ["id"] = $"m-{i}",
            ["memberName"] = $"Member {i} " + new string('x', 100),
            ["joined"] = "2024-01-31",
        }));
        var body = FakeSource.Body(("name", "Big trust"), ("pupils", "50000"), ("members", members));
        output.WriteLine($"Response body: {body.Length / 1024} KiB");

        var timer = Stopwatch.StartNew();
        var first = await Prism.DeliverAsync(Source.Save(app, body));
        var firstElapsed = timer.Elapsed;
        timer.Restart();
        var unchanged = await Prism.DeliverAsync(Source.Save(app, body));
        var unchangedElapsed = timer.Elapsed;
        output.WriteLine($"First projection {firstElapsed.TotalMilliseconds:N0} ms, unchanged re-save {unchangedElapsed.TotalMilliseconds:N0} ms");

        Assert.Equal(ProjectionReasons.NewGeneration, first.Reason);
        Assert.Equal(ProjectionReasons.HashReused, unchanged.Reason);
        var generation = Assert.Single(await Prism.GenerationsAsync(app));
        Assert.Equal(generation.FactCount, await Prism.FactCountAsync(generation.GenerationId));
        var facts = await Prism.CurrentFactsAsync(app);
        Assert.Equal(Members, facts.Count(f => f.FieldId == "memberName"));
        Assert.Equal(Members, facts.Count(f => f.FieldId == "joined"));
        Assert.Contains(facts, f => f.FieldId == "memberName" && f.OccurrencePath == $"members/m-{Members}");
        Assert.True(firstElapsed < TimeSpan.FromSeconds(60), $"Projection took {firstElapsed}.");
    }
}
