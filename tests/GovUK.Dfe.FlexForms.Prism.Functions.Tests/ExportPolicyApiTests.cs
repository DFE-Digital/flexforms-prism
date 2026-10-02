using System.Text;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Reading;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Flattener;
using GovUK.Dfe.FlexForms.Prism.Flattener.Policy;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Functions.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class ExportPolicyApiTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Admin = "ada@example.org (1)";
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid template = Guid.NewGuid();
    private readonly Guid templateVersion = Guid.NewGuid();
    private readonly IAdminAuthenticator authenticator = Substitute.For<IAdminAuthenticator>();

    public async Task InitializeAsync()
    {
        Caller(AdminAuthStatus.Authorized);
        await using var db = sql.CreateContext();
        (string Parent, string Field, bool Collection)[] fields = [("", "name", false), ("", "members", true), ("members", "memberName", false)];
        db.FieldCatalog.AddRange(fields.Select((f, i) => new FieldCatalogEntry
        {
            TenantId = tenant,
            TemplateVersionId = templateVersion,
            TemplateId = template,
            TemplateVersionNumber = "1.0",
            ParentFieldId = f.Parent,
            FieldId = f.Field,
            ContractVersion = PrismVersions.ContractVersion,
            Label = f.Field,
            IsCollection = f.Collection,
            FieldOrder = i,
            ExportStatus = ExportStatus.Unclassified,
            CreatedAt = DateTime.UtcNow,
        }));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ExportPolicyApi Api(Data.PrismDbContext db) => new(
        authenticator, new ExportPolicyService(db, TimeProvider.System), new OperationService(db, TimeProvider.System),
        NullLogger<ExportPolicyApi>.Instance);

    private void Caller(AdminAuthStatus status) =>
        authenticator.AuthenticateAsync(Arg.Any<HttpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AdminAuthResult(status, status == AdminAuthStatus.Authorized ? Admin : null));

    private static HttpRequest Request(string? json = null)
    {
        var context = new DefaultHttpContext();
        if (json is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        }

        return context.Request;
    }

    private async Task<JsonResult> PutAsync(string json)
    {
        await using var db = sql.CreateContext();
        return Assert.IsType<JsonResult>(await Api(db).Change(Request(json), tenant, template, default));
    }

    private async Task<ExportPolicyView> GetAsync()
    {
        await using var db = sql.CreateContext();
        var result = Assert.IsType<JsonResult>(await Api(db).Get(Request(), tenant, template, default));
        return Assert.IsType<ExportPolicyView>(result.Value);
    }

    [Theory]
    [InlineData(AdminAuthStatus.Unauthenticated, 401)]
    [InlineData(AdminAuthStatus.Forbidden, 403)]
    public async Task Callers_without_admin_rights_are_rejected(AdminAuthStatus status, int expected)
    {
        Caller(status);
        await using var db = sql.CreateContext();

        var get = (IStatusCodeActionResult)await Api(db).Get(Request(), tenant, template, default);
        var put = (IStatusCodeActionResult)await Api(db).Change(Request("""{ "decisions": [] }"""), tenant, template, default);

        Assert.Equal(expected, get.StatusCode);
        Assert.Equal(expected, put.StatusCode);
    }

    [Fact]
    public async Task Uncatalogued_template_is_not_found()
    {
        await using var db = sql.CreateContext();

        var result = (IStatusCodeActionResult)await Api(db).Get(Request(), tenant, Guid.NewGuid(), default);

        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task Fields_start_unclassified_and_show_their_decision_once_classified()
    {
        Assert.All((await GetAsync()).Fields, f => Assert.Equal(ExportStatus.Unclassified, f.ExportStatus));

        await PutAsync("""{ "decisions": [ { "fieldId": "name", "decision": "Allowed", "reason": "Not personal" } ] }""");

        var policy = await GetAsync();
        Assert.Equal(1, policy.PolicyVersion);
        var name = policy.Fields.Single(f => f.FieldId == "name");
        Assert.Equal((ExportStatus.Allowed, "Not personal", Admin, 1), (name.ExportStatus, name.Reason, name.DecidedBy, name.PolicyVersion));
        Assert.Equal(ExportStatus.Unclassified, policy.Fields.Single(f => f.FieldId == "members").ExportStatus);
    }

    [Fact]
    public async Task A_change_raises_the_version_and_starts_a_backfill_of_the_tenant()
    {
        var first = Assert.IsType<ExportPolicyChangeResponse>((await PutAsync("""
            { "decisions": [ { "fieldId": "name", "decision": "Allowed" }, { "fieldId": "members", "decision": "Denied" } ] }
            """)).Value);
        var repeated = Assert.IsType<ExportPolicyChangeResponse>((await PutAsync("""
            { "decisions": [ { "fieldId": "name", "decision": "Allowed" } ] }
            """)).Value);
        var denied = Assert.IsType<ExportPolicyChangeResponse>((await PutAsync("""
            { "decisions": [ { "fieldId": "name", "decision": "Denied" } ] }
            """)).Value);

        Assert.Equal((ApplyStatus.Applied, 1, 2), (first.Result.Status, first.Result.PolicyVersion, first.Result.Changed));
        Assert.Equal((OperationKind.Backfill, tenant, Admin), (first.Backfill!.Kind, first.Backfill.TenantId, first.Backfill.RequestedBy));
        Assert.Equal((ApplyStatus.Unchanged, 1), (repeated.Result.Status, repeated.Result.PolicyVersion));
        Assert.Null(repeated.Backfill);
        Assert.Equal((ApplyStatus.Applied, 2), (denied.Result.Status, denied.Result.PolicyVersion));
        Assert.NotNull(denied.Backfill);
    }

    [Fact]
    public async Task Unknown_fields_are_rejected_and_nothing_is_written()
    {
        var result = await PutAsync("""
            { "decisions": [ { "fieldId": "name", "decision": "Allowed" }, { "fieldId": "nmae", "decision": "Allowed" }, { "parentFieldId": "members", "fieldId": "name", "decision": "Allowed" } ] }
            """);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(["nmae", "members.name"], Assert.IsType<ExportPolicyChangeResponse>(result.Value).Result.Problems);
        await using var db = sql.CreateContext();
        Assert.False(await db.FieldExportPolicies.AnyAsync(p => p.TenantId == tenant));
    }

    [Theory]
    [InlineData("""{ "decisions": [ { "fieldId": "name" } ] }""")]
    [InlineData("""{ "decisions": [] }""")]
    [InlineData("""{ }""")]
    [InlineData("""{ "decisions": [ { "fieldId": "name", "decision": "Allowed" }, { "fieldId": "name", "decision": "Denied" } ] }""")]
    [InlineData("""{ "decisions": [ { "fieldId": "name", "decision": 0 } ] }""")]
    [InlineData("""{ "decisions": [ { "fieldId": "name", "decision": "Maybe" } ] }""")]
    public async Task Incomplete_or_ambiguous_decisions_are_rejected(string json)
    {
        var result = await PutAsync(json);

        Assert.Equal(400, result.StatusCode);
        await using var db = sql.CreateContext();
        Assert.False(await db.FieldExportPolicies.AnyAsync(p => p.TenantId == tenant));
    }

    [Fact]
    public async Task Allowing_a_nested_field_without_its_collection_is_flagged()
    {
        var result = Assert.IsType<ExportPolicyChangeResponse>((await PutAsync("""
            { "decisions": [ { "parentFieldId": "members", "fieldId": "memberName", "decision": "Allowed" } ] }
            """)).Value);

        Assert.Contains(result.Result.Warnings, w => w.Contains("members.memberName", StringComparison.Ordinal));
    }

    private async Task<JsonResult> PutDefaultAsync(string json, bool forTemplate = false)
    {
        await using var db = sql.CreateContext();
        var api = Api(db);
        return Assert.IsType<JsonResult>(forTemplate
            ? await api.ChangeTemplateDefault(Request(json), tenant, template, default)
            : await api.ChangeTenantDefault(Request(json), tenant, default));
    }

    private async Task<ExportDefaultView> GetDefaultAsync(bool forTemplate = false)
    {
        await using var db = sql.CreateContext();
        var api = Api(db);
        var result = Assert.IsType<JsonResult>(forTemplate
            ? await api.GetTemplateDefault(Request(), tenant, template, default)
            : await api.GetTenantDefault(Request(), tenant, default));
        return Assert.IsType<ExportDefaultView>(result.Value);
    }

    [Fact]
    public async Task Tenant_export_all_default_marks_undecided_fields_as_exported_and_starts_a_backfill()
    {
        await PutAsync("""{ "decisions": [ { "fieldId": "members", "decision": "Denied" } ] }""");

        var change = Assert.IsType<ExportDefaultChangeResponse>((await PutDefaultAsync("""{ "mode": "ExportAll", "reason": "DPO approved" }""")).Value);
        var repeated = Assert.IsType<ExportDefaultChangeResponse>((await PutDefaultAsync("""{ "mode": "ExportAll", "reason": "DPO approved" }""")).Value);

        Assert.Equal((ApplyStatus.Applied, 2), (change.Result.Status, change.Result.PolicyVersion));
        Assert.Equal((OperationKind.Backfill, tenant), (change.Backfill!.Kind, change.Backfill.TenantId));
        Assert.Equal((ApplyStatus.Unchanged, null), (repeated.Result.Status, repeated.Backfill));
        var tenantDefault = await GetDefaultAsync();
        Assert.Equal((ExportDefaultChoice.ExportAll, DefaultExportMode.ExportAll, ExportDefaultSource.Tenant, "DPO approved", Admin),
            (tenantDefault.Mode, tenantDefault.EffectiveMode, tenantDefault.Source, tenantDefault.Reason, tenantDefault.DecidedBy));

        var policy = await GetAsync();
        Assert.Equal((2, DefaultExportMode.ExportAll, ExportDefaultSource.Tenant), (policy.PolicyVersion, policy.DefaultMode, policy.DefaultSource));
        Assert.Equal(ExportStatus.AllowedByDefault, policy.Fields.Single(f => f.FieldId == "name").ExportStatus);
        Assert.Equal(ExportStatus.Denied, policy.Fields.Single(f => f.FieldId == "members").ExportStatus);
    }

    [Fact]
    public async Task Template_default_overrides_the_tenant_until_set_back_to_inherit()
    {
        await PutDefaultAsync("""{ "mode": "ExportAll" }""");
        await PutDefaultAsync("""{ "mode": "ApproveFirst" }""", forTemplate: true);

        var overridden = await GetDefaultAsync(forTemplate: true);
        Assert.Equal((ExportDefaultChoice.ApproveFirst, DefaultExportMode.ApproveFirst, ExportDefaultSource.Template, 2),
            (overridden.Mode, overridden.EffectiveMode, overridden.Source, overridden.PolicyVersion));
        Assert.Equal(ExportStatus.Unclassified, (await GetAsync()).Fields.Single(f => f.FieldId == "name").ExportStatus);

        var inherit = Assert.IsType<ExportDefaultChangeResponse>((await PutDefaultAsync("""{ "mode": "Inherit" }""", forTemplate: true)).Value);

        Assert.Equal(3, inherit.Result.PolicyVersion);
        var inherited = await GetDefaultAsync(forTemplate: true);
        Assert.Equal((ExportDefaultChoice.Inherit, DefaultExportMode.ExportAll, ExportDefaultSource.Tenant),
            (inherited.Mode, inherited.EffectiveMode, inherited.Source));
        Assert.Equal(3, (await GetAsync()).PolicyVersion);
    }

    [Fact]
    public async Task Tenant_default_change_raises_every_template_above_its_previous_version()
    {
        var other = Guid.NewGuid();
        await using (var db = sql.CreateContext())
        {
            db.FieldExportPolicies.Add(new FieldExportPolicy
            {
                TenantId = tenant, TemplateId = other, FieldId = "x", Decision = ExportDecision.Allowed, PolicyVersion = 9,
                DecidedBy = Admin, DecidedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var change = Assert.IsType<ExportDefaultChangeResponse>((await PutDefaultAsync("""{ "mode": "ExportAll" }""")).Value);

        Assert.Equal(10, change.Result.PolicyVersion);
        Assert.Equal(10, (await GetAsync()).PolicyVersion);
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "mode": "Sometimes" }""")]
    [InlineData("""{ "mode": 2 }""")]
    public async Task Missing_or_unknown_default_mode_is_rejected(string json)
    {
        var result = await PutDefaultAsync(json);

        Assert.Equal(400, result.StatusCode);
        await using var db = sql.CreateContext();
        Assert.False(await db.ExportDefaults.AnyAsync(d => d.TenantId == tenant));
    }

    [Fact]
    public async Task Concurrent_changes_get_distinct_versions()
    {
        async Task<ApplyResult> Apply(string field)
        {
            await using var db = sql.CreateContext();
            return await new ExportPolicyService(db, TimeProvider.System)
                .ApplyAsync(tenant, template, [new ExportDecisionRequest(null, field, ExportDecision.Allowed, null)], Admin, default);
        }

        var results = await Task.WhenAll(Task.Run(() => Apply("name")), Task.Run(() => Apply("members")));

        Assert.Equal([1, 2], results.Select(r => r.PolicyVersion).Order());
    }
}
