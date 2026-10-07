using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Maintenance;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using Microsoft.EntityFrameworkCore;
using static GovUK.Dfe.FlexForms.Prism.Data.Tests.TestData;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class GenerationCleanerTests(SqlServerFixture sql)
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _application = Guid.NewGuid();

    private async Task<WriteResult> Write(Func<ProjectionWriter, Task<WriteResult>> write)
    {
        await using var db = sql.CreateContext();
        return await write(new ProjectionWriter(db, TimeProvider.System));
    }

    private async Task<CleanupResult> Clean(DateTime supersededBefore, int max = 1000)
    {
        await using var db = sql.CreateContext();
        return await new GenerationCleaner(db).DeleteSupersededAsync(supersededBefore, max, default);
    }

    private async Task<List<Guid>> GenerationIds()
    {
        await using var db = sql.CreateContext();
        return await db.ProjectionGenerations.Where(g => g.TenantId == _tenant).Select(g => g.GenerationId).ToListAsync();
    }

    private async Task<int> FactCount(Guid generationId)
    {
        await using var db = sql.CreateContext();
        return await db.AnswerFacts.CountAsync(f => f.GenerationId == generationId);
    }

    [Fact]
    public async Task Superseded_generations_past_retention_are_deleted_with_their_facts()
    {
        var first = await Write(w => w.WriteCurrentAsync(Current(_tenant, _application, 1), [Fact("a", "1"), Fact("b", "2")], default));
        var second = await Write(w => w.WriteCurrentAsync(Current(_tenant, _application, 2), [Fact("a", "3")], default));

        var result = await Clean(DateTime.UtcNow.AddMinutes(1));

        Assert.True(result.GenerationsDeleted >= 1);
        Assert.Equal([second.GenerationId!.Value], await GenerationIds());
        Assert.Equal(0, await FactCount(first.GenerationId!.Value));
        Assert.Equal(1, await FactCount(second.GenerationId.Value));
    }

    [Fact]
    public async Task Superseded_generations_inside_retention_are_kept()
    {
        await Write(w => w.WriteCurrentAsync(Current(_tenant, _application, 1), [Fact("a", "1")], default));
        await Write(w => w.WriteCurrentAsync(Current(_tenant, _application, 2), [Fact("a", "2")], default));

        await Clean(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(2, (await GenerationIds()).Count);
    }

    [Fact]
    public async Task Active_and_selected_generations_are_never_deleted()
    {
        var submissionId = Guid.NewGuid();
        var current = await Write(w => w.WriteCurrentAsync(Current(_tenant, _application, 1), [Fact("a", "1")], default));
        var submission = await Write(w => w.WriteSubmissionAsync(Submission(_tenant, _application, submissionId, 2), [Fact("a", "1")], default));

        // A superseded status alone must not be enough: anything still referenced stays.
        await using (var db = sql.CreateContext())
        {
            await db.ProjectionGenerations
                .Where(g => g.TenantId == _tenant)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GenerationStatus.Superseded).SetProperty(g => g.SupersededAt, DateTime.UtcNow.AddDays(-90)));
        }

        await Clean(DateTime.UtcNow);

        Assert.Equal(
            new[] { current.GenerationId!.Value, submission.GenerationId!.Value }.Order(),
            (await GenerationIds()).Order());
        Assert.Equal(1, await FactCount(current.GenerationId.Value));
    }

    [Fact]
    public async Task Summaries_cover_stored_and_tombstoned_applications_only()
    {
        var deleted = Guid.NewGuid();
        await Write(w => w.WriteCurrentAsync(Current(_tenant, _application, 4, new ProjectionVersions(1, 1, 2)), [], default));
        await Write(w => w.RecordDeletionAsync(new DeletionRecord(_tenant, deleted, 3, DateTime.UtcNow), default));

        await using var db = sql.CreateContext();
        var summaries = await new ProjectionStore(db).GetSummariesAsync(_tenant, [_application, deleted, Guid.NewGuid()], default);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(new StoredSummary(4, ApplicationLifecycle.Draft, new ProjectionVersions(1, 1, 2), false), summaries[_application]);
        Assert.Equal(ApplicationLifecycle.Deleted, summaries[deleted].Lifecycle);
        Assert.True(summaries[deleted].Tombstoned);
    }
}
