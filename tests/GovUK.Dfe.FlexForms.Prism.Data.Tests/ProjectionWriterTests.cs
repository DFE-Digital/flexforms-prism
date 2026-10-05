using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static GovUK.Dfe.FlexForms.Prism.Data.Tests.TestData;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class ProjectionWriterTests(SqlServerFixture sql)
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _application = Guid.NewGuid();

    private ProjectionWriter Writer(PrismDbContext db) => new(db, TimeProvider.System);

    private async Task<WriteResult> WriteCurrent(CurrentProjection projection, params string[] values)
    {
        await using var db = sql.CreateContext();
        var facts = values.Select((v, i) => Fact($"field{i}", v)).ToList();
        return await Writer(db).WriteCurrentAsync(projection, facts, CancellationToken.None);
    }

    private async Task<List<string?>> CurrentValues()
    {
        await using var db = sql.CreateContext();
        return await db.Database
            .SqlQuery<string?>($"SELECT value_string AS Value FROM prism.v_current_answer_facts WHERE tenant_id = {_tenant} AND application_id = {_application} ORDER BY field_id")
            .ToListAsync();
    }

    private async Task<List<ProjectionGeneration>> Generations()
    {
        await using var db = sql.CreateContext();
        return await db.ProjectionGenerations.AsNoTracking()
            .Where(g => g.TenantId == _tenant && g.ApplicationId == _application)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync();
    }

    [Fact]
    public async Task First_write_creates_state_and_an_active_generation()
    {
        var result = await WriteCurrent(Current(_tenant, _application, 1), "a", "b");

        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        Assert.Null(result.SupersededGenerationId);
        Assert.Equal(["a", "b"], await CurrentValues());

        var generation = Assert.Single(await Generations());
        Assert.Equal(GenerationStatus.Active, generation.Status);
        Assert.Equal(2, generation.FactCount);
        Assert.NotNull(generation.ActivatedAt);
    }

    [Fact]
    public async Task Newer_revision_supersedes_the_previous_generation()
    {
        var first = await WriteCurrent(Current(_tenant, _application, 1), "old");
        var second = await WriteCurrent(Current(_tenant, _application, 2), "new");

        Assert.Equal(WriteOutcome.Applied, second.Outcome);
        Assert.Equal(first.GenerationId, second.SupersededGenerationId);
        Assert.Equal(["new"], await CurrentValues());

        var generations = await Generations();
        Assert.Equal([GenerationStatus.Superseded, GenerationStatus.Active], generations.Select(g => g.Status));
        Assert.NotNull(generations[0].SupersededAt);
    }

    [Fact]
    public async Task Older_revision_is_stale_and_writes_nothing()
    {
        await WriteCurrent(Current(_tenant, _application, 10), "ten");

        var result = await WriteCurrent(Current(_tenant, _application, 9), "nine");

        Assert.Equal(WriteOutcome.Stale, result.Outcome);
        Assert.Equal(["ten"], await CurrentValues());
        Assert.Single(await Generations());
    }

    [Fact]
    public async Task Same_revision_with_same_versions_is_stale()
    {
        await WriteCurrent(Current(_tenant, _application, 3), "x");

        var result = await WriteCurrent(Current(_tenant, _application, 3), "y");

        Assert.Equal(WriteOutcome.Stale, result.Outcome);
        Assert.Equal(["x"], await CurrentValues());
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(2, 1, 0)]
    public async Task Same_revision_with_newer_projection_versions_reprojects(int projectorVersion, int contractVersion, int exportPolicyVersion)
    {
        await WriteCurrent(Current(_tenant, _application, 3), "v1");

        var result = await WriteCurrent(
            Current(_tenant, _application, 3, new ProjectionVersions(projectorVersion, contractVersion, exportPolicyVersion)), "v2");

        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        Assert.Equal(["v2"], await CurrentValues());
    }

    [Fact]
    public async Task Same_revision_with_an_older_export_policy_version_is_stale()
    {
        await WriteCurrent(Current(_tenant, _application, 3, new ProjectionVersions(1, 1, 5)), "p5");

        var result = await WriteCurrent(Current(_tenant, _application, 3, new ProjectionVersions(1, 1, 4)), "p4");

        Assert.Equal(WriteOutcome.Stale, result.Outcome);
        Assert.Equal(["p5"], await CurrentValues());
    }

    [Fact]
    public async Task Same_revision_with_an_older_projector_version_is_stale()
    {
        await WriteCurrent(Current(_tenant, _application, 3, new ProjectionVersions(2, 1, 1)), "v2");

        var result = await WriteCurrent(Current(_tenant, _application, 3, V1), "v1");

        Assert.Equal(WriteOutcome.Stale, result.Outcome);
        Assert.Equal(["v2"], await CurrentValues());
    }

    [Fact]
    public async Task Failed_bulk_copy_rolls_back_and_keeps_the_previous_generation_visible()
    {
        await WriteCurrent(Current(_tenant, _application, 1), "kept");

        await using (var db = sql.CreateContext())
        {
            var duplicate = Fact("same", "v");
            await Assert.ThrowsAsync<SqlException>(() =>
                Writer(db).WriteCurrentAsync(Current(_tenant, _application, 2), [duplicate, duplicate], CancellationToken.None));
        }

        Assert.Equal(["kept"], await CurrentValues());
        var generation = Assert.Single(await Generations());
        Assert.Equal(GenerationStatus.Active, generation.Status);
        await using var check = sql.CreateContext();
        var state = await check.ApplicationProjectionStates.AsNoTracking().SingleAsync(s => s.TenantId == _tenant && s.ApplicationId == _application);
        Assert.Equal(1, state.SourceRevision);
    }

    [Fact]
    public async Task Advance_updates_metadata_without_a_new_generation()
    {
        var first = await WriteCurrent(Current(_tenant, _application, 1), "same");

        await using var db = sql.CreateContext();
        var advanced = Current(_tenant, _application, 2, lifecycle: ApplicationLifecycle.Submitted);
        var result = await Writer(db).AdvanceCurrentAsync(advanced, CancellationToken.None);

        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        Assert.Equal(first.GenerationId, result.GenerationId);
        Assert.Single(await Generations());

        await using var check = sql.CreateContext();
        var state = await check.ApplicationProjectionStates.AsNoTracking().SingleAsync(s => s.TenantId == _tenant && s.ApplicationId == _application);
        Assert.Equal(2, state.SourceRevision);
        Assert.Equal(ApplicationLifecycle.Submitted, state.Lifecycle);
        Assert.Equal(advanced.ResponseId, state.ResponseId);
        Assert.Equal(first.GenerationId, state.ActiveGenerationId);
    }

    [Fact]
    public async Task Advance_with_an_older_revision_is_stale()
    {
        await WriteCurrent(Current(_tenant, _application, 5), "x");

        await using var db = sql.CreateContext();
        var result = await Writer(db).AdvanceCurrentAsync(Current(_tenant, _application, 4), CancellationToken.None);

        Assert.Equal(WriteOutcome.Stale, result.Outcome);
    }

    [Fact]
    public async Task Deletion_hides_facts_and_blocks_later_writes()
    {
        await WriteCurrent(Current(_tenant, _application, 1), "visible");

        await using (var db = sql.CreateContext())
        {
            var deleted = await Writer(db).RecordDeletionAsync(new DeletionRecord(_tenant, _application, 2, DateTime.UtcNow), CancellationToken.None);
            Assert.Equal(WriteOutcome.Applied, deleted.Outcome);
        }

        Assert.Empty(await CurrentValues());

        var resurrect = await WriteCurrent(Current(_tenant, _application, 3), "zombie");
        Assert.Equal(WriteOutcome.Tombstoned, resurrect.Outcome);
        Assert.Empty(await CurrentValues());

        await using var check = sql.CreateContext();
        var state = await check.ApplicationProjectionStates.AsNoTracking().SingleAsync(s => s.TenantId == _tenant && s.ApplicationId == _application);
        Assert.Equal(ApplicationLifecycle.Deleted, state.Lifecycle);
        Assert.Equal(2, state.SourceRevision);
    }

    [Fact]
    public async Task Deletion_without_prior_state_creates_a_deleted_state()
    {
        await using var db = sql.CreateContext();
        var writer = Writer(db);

        var first = await writer.RecordDeletionAsync(new DeletionRecord(_tenant, _application, 4, DateTime.UtcNow), CancellationToken.None);
        var duplicate = await writer.RecordDeletionAsync(new DeletionRecord(_tenant, _application, 4, DateTime.UtcNow), CancellationToken.None);

        Assert.Equal(WriteOutcome.Applied, first.Outcome);
        Assert.Equal(WriteOutcome.Stale, duplicate.Outcome);

        await using var check = sql.CreateContext();
        var state = await check.ApplicationProjectionStates.AsNoTracking().SingleAsync(s => s.TenantId == _tenant && s.ApplicationId == _application);
        Assert.Equal(ApplicationLifecycle.Deleted, state.Lifecycle);
        Assert.Null(state.ActiveGenerationId);
    }

    [Fact]
    public async Task Submission_is_written_once_per_version_and_replaced_on_upgrade()
    {
        var submissionId = Guid.NewGuid();
        await using var db = sql.CreateContext();
        var writer = Writer(db);

        var first = await writer.WriteSubmissionAsync(Submission(_tenant, _application, submissionId, 2), [Fact("f", "submitted")], CancellationToken.None);
        var duplicate = await writer.WriteSubmissionAsync(Submission(_tenant, _application, submissionId, 2), [Fact("f", "again")], CancellationToken.None);
        var upgraded = await writer.WriteSubmissionAsync(Submission(_tenant, _application, submissionId, 2, new ProjectionVersions(2, 1, 1)), [Fact("f", "upgraded")], CancellationToken.None);

        Assert.Equal(WriteOutcome.Applied, first.Outcome);
        Assert.Equal(WriteOutcome.Stale, duplicate.Outcome);
        Assert.Equal(WriteOutcome.Applied, upgraded.Outcome);
        Assert.Equal(first.GenerationId, upgraded.SupersededGenerationId);

        await using var check = sql.CreateContext();
        var values = await check.Database
            .SqlQuery<string?>($"SELECT value_string AS Value FROM prism.v_submission_answer_facts WHERE tenant_id = {_tenant} AND submission_id = {submissionId}")
            .ToListAsync();
        Assert.Equal(["upgraded"], values);
    }

    [Fact]
    public async Task Submission_facts_are_hidden_once_the_application_is_deleted()
    {
        var submissionId = Guid.NewGuid();
        await using var db = sql.CreateContext();
        var writer = Writer(db);

        await writer.WriteSubmissionAsync(Submission(_tenant, _application, submissionId, 2), [Fact("f", "submitted")], CancellationToken.None);
        await writer.RecordDeletionAsync(new DeletionRecord(_tenant, _application, 3, DateTime.UtcNow), CancellationToken.None);

        await using var check = sql.CreateContext();
        var count = await check.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM prism.v_submission_answer_facts WHERE tenant_id = {_tenant} AND submission_id = {submissionId}")
            .SingleAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Referenced_generations_cannot_be_deleted()
    {
        var result = await WriteCurrent(Current(_tenant, _application, 1), "x");

        await using var db = sql.CreateContext();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync(
            $"DELETE FROM prism.projection_generations WHERE generation_id = {result.GenerationId!.Value}"));
    }

    [Fact]
    public async Task Concurrent_writers_for_one_application_leave_the_highest_revision_active()
    {
        var writes = Enumerable.Range(1, 8)
            .Select(revision => Task.Run(() => WriteCurrent(Current(_tenant, _application, revision), $"r{revision}")))
            .ToArray();

        await Task.WhenAll(writes);

        Assert.Equal(["r8"], await CurrentValues());
        var generations = await Generations();
        Assert.Single(generations, g => g.Status == GenerationStatus.Active);
        Assert.DoesNotContain(generations, g => g.Status == GenerationStatus.Building);
    }

    [Fact]
    public async Task Write_chosen_as_deadlock_victim_is_retried_and_applied()
    {
        await WriteCurrent(Current(_tenant, _application, 1), "before");

        await using var rival = new SqlConnection(sql.ConnectionString);
        await rival.OpenAsync();
        await using var rivalTransaction = (SqlTransaction)await rival.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await RivalExecuteAsync("SET DEADLOCK_PRIORITY HIGH");
        await RivalExecuteAsync("SELECT TOP (0) id FROM prism.answer_facts WITH (TABLOCKX, HOLDLOCK)");

        var write = Task.Run(() => WriteCurrent(Current(_tenant, _application, 2), "after"));

        while (!write.IsCompleted && (int)(await RivalScalarAsync("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @@SPID"))! == 0)
        {
            await Task.Delay(50);
        }

        await RivalScalarAsync(
            $"SELECT source_revision FROM prism.application_projection_state WITH (UPDLOCK) WHERE tenant_id = '{_tenant}' AND application_id = '{_application}'");
        await rivalTransaction.RollbackAsync();

        var result = await write;
        Assert.Equal(WriteOutcome.Applied, result.Outcome);
        Assert.Equal(["after"], await CurrentValues());
        Assert.DoesNotContain(await Generations(), g => g.Status == GenerationStatus.Building);

        Task RivalExecuteAsync(string text) => new SqlCommand(text, rival, rivalTransaction).ExecuteNonQueryAsync();
        Task<object?> RivalScalarAsync(string text) => new SqlCommand(text, rival, rivalTransaction).ExecuteScalarAsync();
    }
}
