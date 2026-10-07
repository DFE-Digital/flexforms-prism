using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

namespace GovUK.Dfe.FlexForms.Prism.Scenario.Tests;

/// <summary>Projector and export policy changes re-project without a new source revision.</summary>
public sealed class VersioningScenarios(SqlServerFixture sql) : ScenarioBase(sql)
{
    [Fact]
    public async Task Projector_upgrade_reprojects_the_same_revision_through_reconciliation()
    {
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));
        var original = Assert.Single(await Prism.GenerationsAsync(app));

        // Make the stored projection look like it came from an older projector, whose hash would differ.
        await Prism.ExecuteSqlAsync($"""
            UPDATE prism.application_projection_state SET projector_version = 0, source_hash = 0x00 WHERE application_id = {app};
            UPDATE prism.projection_generations SET projector_version = 0 WHERE application_id = {app};
            """);

        var (_, enqueued) = await Prism.RunOperationAsync(OperationKind.Reconciliation);

        Assert.Contains(enqueued, e => e.ApplicationId == app && e.SourceRevision == original.SourceRevision);
        var generations = await Prism.GenerationsAsync(app);
        Assert.Equal(2, generations.Count);
        var upgraded = Assert.Single(generations, g => g.Status == GenerationStatus.Active);
        Assert.Equal(original.SourceRevision, upgraded.SourceRevision);
        Assert.Equal(PrismVersions.ProjectorVersion, upgraded.ProjectorVersion);
        Assert.Equal(GenerationStatus.Superseded, generations.Single(g => g.GenerationId == original.GenerationId).Status);
        Assert.Equal("Ada", NameIn(await Prism.CurrentFactsAsync(app)));
    }

    [Fact]
    public async Task Field_with_no_export_decision_is_catalogued_but_never_exported()
    {
        var app = Guid.NewGuid();

        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        var facts = await Prism.CurrentFactsAsync(app);
        Assert.Equal("Ada", NameIn(facts));
        Assert.DoesNotContain(facts, f => f.FieldId == "secret");
        Assert.DoesNotContain(facts, f => f.Value == "do not export");
        var catalog = await Prism.CatalogAsync();
        Assert.Equal(ExportStatus.Unclassified, catalog.Single(c => c.FieldId == "secret").ExportStatus);
        Assert.Equal(ExportStatus.Allowed, catalog.Single(c => c.FieldId == "name").ExportStatus);
    }

    [Fact]
    public async Task Unclassified_tenant_exports_nothing()
    {
        await Prism.ClearPolicyAsync();
        var app = Guid.NewGuid();

        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        Assert.Empty(await Prism.CurrentFactsAsync(app));
        Assert.All(await Prism.CatalogAsync(), c => Assert.Equal(ExportStatus.Unclassified, c.ExportStatus));
    }

    [Fact]
    public async Task Allowing_a_field_reprojects_existing_applications_through_backfill()
    {
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        await Prism.ClassifyAsync(policyVersion: 2, allowSecret: true);
        await Prism.RunOperationAsync(OperationKind.Backfill);

        var facts = await Prism.CurrentFactsAsync(app);
        Assert.Contains(facts, f => f.FieldId == "secret" && f.Value == "do not export");
        var state = (await Prism.StateAsync(app))!;
        Assert.Equal(1, state.SourceRevision);
        Assert.Equal(2, state.ExportPolicyVersion);
        Assert.Equal(ExportStatus.Allowed, (await Prism.CatalogAsync()).Single(c => c.FieldId == "secret").ExportStatus);
    }

    [Fact]
    public async Task Denying_a_field_removes_its_facts_after_the_backfill()
    {
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        var change = await Prism.ChangePolicyAsync(new ExportDecisionRequest(null, "name", ExportDecision.Denied, "Personal data"));
        await Prism.RunOperationAsync(OperationKind.Backfill);

        Assert.Equal(ApplyStatus.Applied, change.Status);
        var facts = await Prism.CurrentFactsAsync(app);
        Assert.DoesNotContain(facts, f => f.FieldId == "name");
        Assert.Contains(facts, f => f.FieldId == "pupils");
        Assert.Equal(change.PolicyVersion, (await Prism.StateAsync(app))!.ExportPolicyVersion);
        Assert.Equal(ExportStatus.Denied, (await Prism.CatalogAsync()).Single(c => c.FieldId == "name").ExportStatus);
    }

    [Fact]
    public async Task Export_all_default_exports_undecided_fields_but_not_denied_ones_after_the_backfill()
    {
        await Prism.ClearPolicyAsync();
        var app = Guid.NewGuid();
        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));
        Assert.Empty(await Prism.CurrentFactsAsync(app));

        await Prism.ChangePolicyAsync(new ExportDecisionRequest(null, "secret", ExportDecision.Denied, "Personal data"));
        var change = await Prism.SetDefaultAsync(ExportDefaultChoice.ExportAll);
        await Prism.RunOperationAsync(OperationKind.Backfill);

        Assert.Equal((ApplyStatus.Applied, 2), (change.Status, change.PolicyVersion));
        var facts = await Prism.CurrentFactsAsync(app);
        Assert.Equal("Ada", NameIn(facts));
        Assert.Contains(facts, f => f.FieldId == "pupils");
        Assert.DoesNotContain(facts, f => f.FieldId == "secret");
        Assert.Equal(2, (await Prism.StateAsync(app))!.ExportPolicyVersion);
        var catalog = await Prism.CatalogAsync();
        Assert.Equal(ExportStatus.AllowedByDefault, catalog.Single(c => c.FieldId == "name").ExportStatus);
        Assert.Equal(ExportStatus.Denied, catalog.Single(c => c.FieldId == "secret").ExportStatus);
    }

    [Fact]
    public async Task Template_override_wins_over_the_tenant_default()
    {
        await Prism.ClearPolicyAsync();
        var app = Guid.NewGuid();
        await Prism.SetDefaultAsync(ExportDefaultChoice.ExportAll);
        await Prism.SetDefaultAsync(ExportDefaultChoice.ApproveFirst, forTemplate: true);

        await Prism.DeliverAsync(Source.Save(app, Named("Ada")));

        Assert.Empty(await Prism.CurrentFactsAsync(app));
        Assert.All(await Prism.CatalogAsync(), c => Assert.Equal(ExportStatus.Unclassified, c.ExportStatus));
    }
}
