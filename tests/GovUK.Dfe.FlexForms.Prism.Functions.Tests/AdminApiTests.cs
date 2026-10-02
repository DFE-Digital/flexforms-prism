using System.Text;
using GovUK.Dfe.CoreLibs.Contracts.ExternalApplications.Models.Response;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Data.Tests;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Functions.Functions;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class AdminApiTests(SqlServerFixture sql)
{
    private readonly Guid tenant = Guid.NewGuid();
    private readonly IAdminAuthenticator authenticator = Substitute.For<IAdminAuthenticator>();
    private readonly ISourceClient source = Substitute.For<ISourceClient>();

    private AdminApi Api(Data.PrismDbContext db)
    {
        source.GetTenantsAsync(Arg.Any<CancellationToken>()).Returns([new PrismTenantDto(tenant, "Tenant")]);
        return new AdminApi(authenticator, new OperationService(db, TimeProvider.System), source, NullLogger<AdminApi>.Instance);
    }

    private void Caller(AdminAuthStatus status) =>
        authenticator.AuthenticateAsync(Arg.Any<HttpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AdminAuthResult(status, status == AdminAuthStatus.Authorized ? "ada@example.org (1)" : null));

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

    [Theory]
    [InlineData(AdminAuthStatus.Unauthenticated, 401)]
    [InlineData(AdminAuthStatus.Forbidden, 403)]
    public async Task Callers_without_admin_rights_are_rejected(AdminAuthStatus status, int expected)
    {
        Caller(status);
        await using var db = sql.CreateContext();

        var result = (IStatusCodeActionResult)await Api(db).CreateBackfill(Request("{}"), default);

        Assert.Equal(expected, result.StatusCode);
        Assert.Equal(expected, ((IStatusCodeActionResult)await Api(db).Get(Request(), Guid.NewGuid(), default)).StatusCode);
    }

    [Fact]
    public async Task Creating_a_backfill_records_the_caller_and_returns_its_location()
    {
        Caller(AdminAuthStatus.Authorized);
        await using var db = sql.CreateContext();
        var request = Request($$"""{ "tenantId": "{{tenant}}", "modifiedSince": "2026-01-01T00:00:00Z" }""");

        var result = Assert.IsType<JsonResult>(await Api(db).CreateBackfill(request, default));

        Assert.Equal(202, result.StatusCode);
        var operation = Assert.IsType<OperationView>(result.Value);
        Assert.Equal((OperationKind.Backfill, tenant, "ada@example.org (1)", BackfillStatus.Pending),
            (operation.Kind, operation.TenantId, operation.RequestedBy, operation.Status));
        Assert.Equal($"/api/control/backfill/{operation.OperationId}", request.HttpContext.Response.Headers.Location.ToString());

        var fetched = Assert.IsType<JsonResult>(await Api(db).Get(Request(), operation.OperationId, default));
        Assert.Equal(operation, fetched.Value);
    }

    [Fact]
    public async Task A_reconciliation_can_be_requested_on_demand()
    {
        Caller(AdminAuthStatus.Authorized);
        await using var db = sql.CreateContext();

        var result = Assert.IsType<JsonResult>(await Api(db).CreateReconciliation(Request("{}"), default));

        Assert.Equal(OperationKind.Reconciliation, Assert.IsType<OperationView>(result.Value).Kind);
    }

    [Theory]
    [InlineData(null, 415)]
    [InlineData("not json", 400)]
    [InlineData("null", 400)]
    public async Task Bad_bodies_are_rejected(string? body, int expected)
    {
        Caller(AdminAuthStatus.Authorized);
        await using var db = sql.CreateContext();

        var result = (IStatusCodeActionResult)await Api(db).CreateBackfill(Request(body), default);

        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task An_unknown_tenant_is_rejected()
    {
        Caller(AdminAuthStatus.Authorized);
        await using var db = sql.CreateContext();

        var result = (IStatusCodeActionResult)await Api(db).CreateBackfill(Request($$"""{ "tenantId": "{{Guid.NewGuid()}}" }"""), default);

        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task Cancelling_records_who_cancelled_and_conflicts_once_finished()
    {
        Caller(AdminAuthStatus.Authorized);
        await using var db = sql.CreateContext();
        var created = (OperationView)((JsonResult)await Api(db).CreateBackfill(Request("{}"), default)).Value!;

        var cancelled = Assert.IsType<JsonResult>(await Api(db).Cancel(Request(), created.OperationId, default));
        var again = Assert.IsType<JsonResult>(await Api(db).Cancel(Request(), created.OperationId, default));

        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal((BackfillStatus.Cancelled, "ada@example.org (1)"), (((OperationView)cancelled.Value!).Status, ((OperationView)cancelled.Value!).CancelledBy));
        Assert.Equal(409, again.StatusCode);
        Assert.IsType<NotFoundResult>(await Api(db).Cancel(Request(), Guid.NewGuid(), default));
    }
}
