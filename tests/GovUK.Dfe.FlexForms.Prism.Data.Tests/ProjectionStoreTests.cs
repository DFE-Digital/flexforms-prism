using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Writing;
using GovUK.Dfe.FlexForms.Prism.Flattener.Catalogue;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using static GovUK.Dfe.FlexForms.Prism.Data.Tests.TestData;

namespace GovUK.Dfe.FlexForms.Prism.Data.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class ProjectionStoreTests(SqlServerFixture sql)
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _application = Guid.NewGuid();

    private async Task<T> WithStore<T>(Func<ProjectionStore, Task<T>> read)
    {
        await using var db = sql.CreateContext();
        return await read(new ProjectionStore(db));
    }

    private async Task<WriteResult> Write(Func<ProjectionWriter, Task<WriteResult>> write)
    {
        await using var db = sql.CreateContext();
        return await write(new ProjectionWriter(db, TimeProvider.System));
    }

    [Fact]
    public async Task Unknown_application_has_no_state_and_no_tombstone()
    {
        Assert.Null(await WithStore(s => s.GetStateAsync(_tenant, _application, default)));
        Assert.False(await WithStore(s => s.IsTombstonedAsync(_tenant, _application, default)));
    }

    [Fact]
    public async Task State_reflects_the_latest_write()
    {
        var projection = Current(_tenant, _application, 4, new ProjectionVersions(1, 1, 3), ApplicationLifecycle.Submitted, "seed");
        var written = await Write(w => w.WriteCurrentAsync(projection, [Fact("f", "v")], default));

        var state = await WithStore(s => s.GetStateAsync(_tenant, _application, default));

        Assert.NotNull(state);
        Assert.Equal(4, state.SourceRevision);
        Assert.Equal(ApplicationLifecycle.Submitted, state.Lifecycle);
        Assert.Equal(written.GenerationId, state.ActiveGenerationId);
        Assert.Equal(projection.SourceHash, state.SourceHash);
        Assert.Equal(projection.TemplateId, state.TemplateId);
        Assert.Equal(new ProjectionVersions(1, 1, 3), state.Versions);
    }

    [Fact]
    public async Task Deletion_is_visible_as_a_tombstone()
    {
        await Write(w => w.RecordDeletionAsync(new DeletionRecord(_tenant, _application, 2, DateTime.UtcNow), default));

        Assert.True(await WithStore(s => s.IsTombstonedAsync(_tenant, _application, default)));
        Assert.Equal(ApplicationLifecycle.Deleted, (await WithStore(s => s.GetStateAsync(_tenant, _application, default)))!.Lifecycle);
    }

    [Fact]
    public async Task Submission_snapshot_is_readable_by_submission_id()
    {
        var submissionId = Guid.NewGuid();
        var written = await Write(w => w.WriteSubmissionAsync(Submission(_tenant, _application, submissionId, 5), [Fact("f", "v")], default));

        var stored = await WithStore(s => s.GetSubmissionAsync(_tenant, submissionId, default));

        Assert.Equal(new StoredSubmission(written.GenerationId!.Value, 5, V1), stored);
        Assert.Null(await WithStore(s => s.GetSubmissionAsync(Guid.NewGuid(), submissionId, default)));
    }

    [Fact]
    public async Task Export_policy_without_decisions_denies_everything_at_version_zero()
    {
        var policy = await WithStore(s => s.GetExportPolicyAsync(_tenant, Guid.NewGuid(), default));

        Assert.Equal(0, policy.Version);
        Assert.False(policy.IsExported(new CatalogField { FieldId = "name" }));
    }

    [Fact]
    public async Task Export_policy_version_is_the_highest_decision_version()
    {
        var templateId = Guid.NewGuid();
        await using (var db = sql.CreateContext())
        {
            db.FieldExportPolicies.AddRange(
                Decision(templateId, "name", ExportDecision.Allowed, 2),
                Decision(templateId, "secret", ExportDecision.Denied, 7));
            await db.SaveChangesAsync();
        }

        var policy = await WithStore(s => s.GetExportPolicyAsync(_tenant, templateId, default));

        Assert.Equal(7, policy.Version);
        Assert.True(policy.IsExported(new CatalogField { FieldId = "name" }));
        Assert.False(policy.IsExported(new CatalogField { FieldId = "secret" }));
    }

    [Fact]
    public async Task Export_default_comes_from_the_template_then_the_tenant_then_approve_first()
    {
        var overridden = Guid.NewGuid();
        var inheriting = Guid.NewGuid();
        var explicitInherit = Guid.NewGuid();
        await using (var db = sql.CreateContext())
        {
            db.ExportDefaults.AddRange(
                Default(Guid.Empty, DefaultExportMode.ExportAll, 3),
                Default(overridden, DefaultExportMode.ApproveFirst, 5),
                Default(explicitInherit, null, 4));
            db.FieldExportPolicies.Add(Decision(inheriting, "secret", ExportDecision.Denied, 2));
            await db.SaveChangesAsync();
        }

        var templateWins = await WithStore(s => s.GetExportPolicyAsync(_tenant, overridden, default));
        var tenantApplies = await WithStore(s => s.GetExportPolicyAsync(_tenant, inheriting, default));
        var nullInherits = await WithStore(s => s.GetExportPolicyAsync(_tenant, explicitInherit, default));
        var builtIn = await WithStore(s => s.GetExportPolicyAsync(Guid.NewGuid(), inheriting, default));

        Assert.Equal((DefaultExportMode.ApproveFirst, 5), (templateWins.DefaultMode, templateWins.Version));
        Assert.Equal((DefaultExportMode.ExportAll, 3), (tenantApplies.DefaultMode, tenantApplies.Version));
        Assert.True(tenantApplies.IsExported(new CatalogField { FieldId = "name" }));
        Assert.False(tenantApplies.IsExported(new CatalogField { FieldId = "secret" }));
        Assert.Equal((DefaultExportMode.ExportAll, 4), (nullInherits.DefaultMode, nullInherits.Version));
        Assert.Equal((DefaultExportMode.ApproveFirst, 0), (builtIn.DefaultMode, builtIn.Version));
    }

    private ExportDefault Default(Guid templateId, DefaultExportMode? mode, int version) => new()
    {
        TenantId = _tenant,
        TemplateId = templateId,
        Mode = mode,
        PolicyVersion = version,
        DecidedBy = "test",
        DecidedAt = DateTime.UtcNow,
    };

    private FieldExportPolicy Decision(Guid templateId, string fieldId, ExportDecision decision, int version) => new()
    {
        TenantId = _tenant,
        TemplateId = templateId,
        FieldId = fieldId,
        Decision = decision,
        PolicyVersion = version,
        DecidedBy = "test",
        DecidedAt = DateTime.UtcNow,
    };
}
