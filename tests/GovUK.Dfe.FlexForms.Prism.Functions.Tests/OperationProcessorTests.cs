using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Enums;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Enums;
using GovUK.Dfe.CoreLibs.Messaging.Contracts.Messages.Events;
using GovUK.Dfe.FlexForms.Prism.Data;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Functions.Messaging;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class OperationProcessorTests : IAsyncLifetime
{
    private readonly SqlServerFixture sql;
    private readonly ISourceClient source = Substitute.For<ISourceClient>();
    private readonly List<ApplicationProjectionRequestedEvent> sent = [];
    private readonly ManualClock clock = new();
    private readonly Guid tenant = Guid.NewGuid();
    private Guid[] excludedTenants = [];
    private int maxMessagesPerMinute;
    private readonly List<int> sendSizes = [];

    public OperationProcessorTests(SqlServerFixture sql)
    {
        this.sql = sql;
        source.GetTenantsAsync(Arg.Any<CancellationToken>()).Returns([new PrismTenantDto(tenant, "Tenant")]);
    }

    public async Task InitializeAsync()
    {
        await using var db = sql.CreateContext();
        await db.BackfillOperations.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Run()
    {
        await using var db = sql.CreateContext();
        var sender = Substitute.For<IProjectionRequestSender>();
        sender.SendAsync(
                Arg.Do<IReadOnlyCollection<ApplicationProjectionRequestedEvent>>(m =>
                {
                    sent.AddRange(m);
                    sendSizes.Add(m.Count);
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var options = Options.Create(new PrismFunctionsOptions
        {
            ExcludedTenantIds = excludedTenants,
            Backfill = { PageSize = 2, TimeBudget = TimeSpan.FromMinutes(1), MaxMessagesPerMinute = maxMessagesPerMinute },
        });
        await new OperationProcessor(
                db, source, new ProjectionStore(db), sender, new ControlPlaneMetrics(new TestMeterFactory()), clock, options,
                NullLogger<OperationProcessor>.Instance)
            .RunAsync(default);
    }

    private async Task<OperationView> Create(OperationKind kind, Guid? tenantId = null)
    {
        await using var db = sql.CreateContext();
        return await new OperationService(db, TimeProvider.System).CreateAsync(kind, tenantId, null, "test", default);
    }

    private async Task<OperationView> Load(Guid operationId)
    {
        await using var db = sql.CreateContext();
        return (await new OperationService(db, TimeProvider.System).GetAsync(operationId, default))!;
    }

    private void Pages(Guid tenantId, params PrismApplicationSummaryDto[][] pages)
    {
        for (var i = 0; i < pages.Length; i++)
        {
            source.ListApplicationsAsync(tenantId, null, i + 1, 2, Arg.Any<CancellationToken>())
                .Returns(new PrismApplicationPageDto(pages[i], i + 1, 2, i < pages.Length - 1));
        }
    }

    private static PrismApplicationSummaryDto App(long revision, bool deleted = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), revision, deleted ? ApplicationStatus.Deleted : ApplicationStatus.InProgress, deleted, DateTime.UtcNow);

    [Fact]
    public async Task A_backfill_enqueues_a_resync_for_every_application_on_every_page()
    {
        var apps = new[] { App(1), App(2), App(3) };
        Pages(tenant, [apps[0], apps[1]], [apps[2]]);
        var operation = await Create(OperationKind.Backfill, tenant);

        await Run();

        Assert.Equal(apps.Select(a => (a.ApplicationId, a.SourceRevision)), sent.Select(m => (m.ApplicationId, m.SourceRevision)));
        Assert.All(sent, m =>
        {
            Assert.Equal(ProjectionReason.Resync, m.Reason);
            Assert.Equal(tenant, m.TenantId);
            Assert.Equal(operation.OperationId, m.OperationId);
        });

        var finished = await Load(operation.OperationId);
        Assert.Equal(BackfillStatus.Completed, finished.Status);
        Assert.Equal((2, 3, 3), (finished.PagesProcessed, finished.ApplicationsScanned, finished.MessagesEnqueued));
        Assert.NotNull(finished.CompletedAt);
    }

    [Fact]
    public async Task Resyncs_are_sent_in_slices_paced_to_the_configured_rate()
    {
        maxMessagesPerMinute = 24;
        var apps = new[] { App(1), App(1), App(1), App(1), App(1) };
        Pages(tenant, [apps[0], apps[1]], [apps[2], apps[3]], [apps[4]]);
        await Create(OperationKind.Backfill, tenant);

        await Run();

        Assert.Equal(apps.Select(a => a.ApplicationId), sent.Select(m => m.ApplicationId));
        Assert.Equal([2, 2, 1], sendSizes);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5)], clock.Delays);
    }

    [Fact]
    public async Task A_tenant_backfill_request_reuses_one_that_has_not_started()
    {
        var first = await RequestTenantBackfill();
        var second = await RequestTenantBackfill();

        Assert.Equal(first.OperationId, second.OperationId);
    }

    [Fact]
    public async Task A_tenant_backfill_request_supersedes_a_running_one()
    {
        var running = await RequestTenantBackfill();
        await using (var db = sql.CreateContext())
        {
            await db.BackfillOperations.Where(o => o.OperationId == running.OperationId)
                .ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, BackfillStatus.Running));
        }

        var replacement = await RequestTenantBackfill();

        Assert.NotEqual(running.OperationId, replacement.OperationId);
        var superseded = await Load(running.OperationId);
        Assert.Equal((BackfillStatus.Cancelled, $"superseded by {replacement.OperationId}"), (superseded.Status, superseded.CancelledBy));
        Assert.Equal(BackfillStatus.Pending, (await Load(replacement.OperationId)).Status);
    }

    [Fact]
    public async Task A_tenant_backfill_request_leaves_other_tenants_alone()
    {
        var other = await Create(OperationKind.Backfill, Guid.NewGuid());

        var requested = await RequestTenantBackfill();

        Assert.NotEqual(other.OperationId, requested.OperationId);
        Assert.Equal(BackfillStatus.Pending, (await Load(other.OperationId)).Status);
    }

    private async Task<OperationView> RequestTenantBackfill()
    {
        await using var db = sql.CreateContext();
        return await new OperationService(db, TimeProvider.System).RequestTenantBackfillAsync(tenant, "test", default);
    }

    [Fact]
    public async Task An_all_tenant_backfill_covers_every_source_tenant()
    {
        var other = Guid.NewGuid();
        source.GetTenantsAsync(Arg.Any<CancellationToken>()).Returns([new PrismTenantDto(tenant, "A"), new PrismTenantDto(other, "B")]);
        Pages(tenant, [App(1)]);
        Pages(other, [App(1)]);
        await Create(OperationKind.Backfill);

        await Run();

        Assert.Equal(new[] { tenant, other }.Order(), sent.Select(m => m.TenantId).Order());
    }

    [Fact]
    public async Task Excluded_tenants_are_skipped()
    {
        var other = Guid.NewGuid();
        excludedTenants = [other];
        source.GetTenantsAsync(Arg.Any<CancellationToken>()).Returns([new PrismTenantDto(tenant, "A"), new PrismTenantDto(other, "B")]);
        Pages(tenant, [App(1)]);
        Pages(other, [App(1)]);
        var all = await Create(OperationKind.Backfill);
        var single = await Create(OperationKind.Backfill, other);

        await Run();

        Assert.Equal([tenant], sent.Select(m => m.TenantId));
        Assert.Equal(BackfillStatus.Completed, (await Load(all.OperationId)).Status);
        Assert.Equal(BackfillStatus.Completed, (await Load(single.OperationId)).Status);
        await source.DidNotReceive().ListApplicationsAsync(other, Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reconciliation_enqueues_only_drifted_applications()
    {
        var current = App(5);
        var behind = App(5);
        var missing = App(2);
        var unrecordedDeletion = App(6, deleted: true);
        var recordedDeletion = App(4, deleted: true);
        await using (var db = sql.CreateContext())
        {
            var writer = new ProjectionWriter(db, TimeProvider.System);
            await writer.WriteCurrentAsync(Current(current.ApplicationId, 5), [], default);
            await writer.WriteCurrentAsync(Current(behind.ApplicationId, 3), [], default);
            await writer.WriteCurrentAsync(Current(unrecordedDeletion.ApplicationId, 5), [], default);
            await writer.RecordDeletionAsync(new DeletionRecord(tenant, recordedDeletion.ApplicationId, 4, DateTime.UtcNow), default);
        }

        Pages(tenant, [current, behind], [missing, unrecordedDeletion], [recordedDeletion]);
        var operation = await Create(OperationKind.Reconciliation, tenant);

        await Run();

        Assert.Equal(
            new[] { behind.ApplicationId, missing.ApplicationId, unrecordedDeletion.ApplicationId }.Order(),
            sent.Select(m => m.ApplicationId).Order());
        var finished = await Load(operation.OperationId);
        Assert.Equal((5, 3), (finished.ApplicationsScanned, finished.MessagesEnqueued));
    }

    [Fact]
    public async Task A_run_that_exhausts_its_budget_resumes_from_the_saved_page()
    {
        var apps = new[] { App(1), App(1), App(1) };
        Pages(tenant, [apps[0], apps[1]], [apps[2]]);
        source.ListApplicationsAsync(tenant, null, 1, 2, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                clock.Advance(TimeSpan.FromMinutes(2));
                return new PrismApplicationPageDto([apps[0], apps[1]], 1, 2, true);
            });
        var operation = await Create(OperationKind.Backfill, tenant);

        await Run();
        var paused = await Load(operation.OperationId);
        Assert.Equal((BackfillStatus.Running, 1), (paused.Status, paused.PagesProcessed));

        await Run();
        Assert.Equal(BackfillStatus.Completed, (await Load(operation.OperationId)).Status);
        Assert.Equal(apps.Select(a => a.ApplicationId), sent.Select(m => m.ApplicationId));
        await source.Received(1).ListApplicationsAsync(tenant, null, 1, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cancelled_operation_stops_after_the_current_page()
    {
        var operation = await Create(OperationKind.Backfill, tenant);
        source.ListApplicationsAsync(tenant, null, 1, 2, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var db = sql.CreateContext();
                await new OperationService(db, TimeProvider.System).CancelAsync(operation.OperationId, "ops", default);
                return new PrismApplicationPageDto([App(1)], 1, 2, true);
            });

        await Run();

        var cancelled = await Load(operation.OperationId);
        Assert.Equal((BackfillStatus.Cancelled, "ops", 0), (cancelled.Status, cancelled.CancelledBy, cancelled.PagesProcessed));
        await source.DidNotReceive().ListApplicationsAsync(tenant, null, 2, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_permanent_source_failure_fails_the_operation()
    {
        source.ListApplicationsAsync(tenant, null, 1, 2, Arg.Any<CancellationToken>()).ThrowsAsync(new SourceRejectedException("bad request", 400));
        var operation = await Create(OperationKind.Backfill, tenant);

        await Run();

        var failed = await Load(operation.OperationId);
        Assert.Equal((BackfillStatus.Failed, "bad request"), (failed.Status, failed.Error));
    }

    [Fact]
    public async Task A_transient_source_failure_leaves_the_operation_running_with_the_error()
    {
        source.ListApplicationsAsync(tenant, null, 1, 2, Arg.Any<CancellationToken>()).ThrowsAsync(new SourceUnavailableException("down", 503));
        var operation = await Create(OperationKind.Backfill, tenant);

        await Run();

        var interrupted = await Load(operation.OperationId);
        Assert.Equal((BackfillStatus.Running, "down"), (interrupted.Status, interrupted.Error));
    }

    [Theory]
    [InlineData(PrismVersions.ProjectorVersion - 1, PrismVersions.ContractVersion, "outdated")]
    [InlineData(PrismVersions.ProjectorVersion, PrismVersions.ContractVersion - 1, "outdated")]
    [InlineData(PrismVersions.ProjectorVersion, PrismVersions.ContractVersion, null)]
    public void Older_projector_or_contract_versions_count_as_drift(int projectorVersion, int contractVersion, string? expected)
    {
        var stored = new StoredSummary(5, ApplicationLifecycle.Draft, new ProjectionVersions(projectorVersion, contractVersion, 9), false);

        Assert.Equal(expected, OperationProcessor.DriftOf(App(5), stored));
    }

    private CurrentProjection Current(Guid applicationId, long revision) => new(
        tenant, applicationId, revision, Guid.NewGuid(), revision, Guid.NewGuid(), Guid.NewGuid(), ApplicationLifecycle.Draft,
        new byte[32], new ProjectionVersions(PrismVersions.ProjectorVersion, PrismVersions.ContractVersion, 0), DateTime.UtcNow);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public List<TimeSpan> Delays { get; } = [];

        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => now;

        /// <summary>Fires straight away, moving the clock on by the delay, so paced sends run instantly.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
