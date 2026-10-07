using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using static GovUK.Dfe.FlexForms.Prism.Functions.Functions.AdminResponses;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Functions;

public sealed record ExportPolicyChangeRequest(IReadOnlyList<ExportDecisionRequest>? Decisions);

public sealed record ExportPolicyChangeResponse(ApplyResult Result, OperationView? Backfill);

public sealed record ExportDefaultChangeResponse(DefaultChangeResult Result, OperationView? Backfill);

/// <summary>
/// Lets the data owner classify a template's fields and choose what happens to fields nobody has classified, for
/// the whole tenant or one template. A change starts a backfill of the tenant so existing projections pick up the
/// new policy; until it finishes, the views can still show the previous classification.
/// </summary>
public sealed partial class ExportPolicyApi(
    IAdminAuthenticator authenticator,
    ExportPolicyService policies,
    OperationService operations,
    ILogger<ExportPolicyApi> logger)
{
    private const string Route = "control/tenants/{tenantId:guid}/templates/{templateId:guid}/export-policy";
    private const string TenantDefaultRoute = "control/tenants/{tenantId:guid}/export-default";
    private const string TemplateDefaultRoute = "control/tenants/{tenantId:guid}/templates/{templateId:guid}/export-default";

    [Function("GetExportPolicy")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Route)] HttpRequest request,
        Guid tenantId,
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        var policy = await policies.GetAsync(tenantId, templateId, cancellationToken);
        return policy is null
            ? Problem(StatusCodes.Status404NotFound, "Nothing has been projected for this template yet, so its fields are not catalogued.")
            : Json(policy, StatusCodes.Status200OK);
    }

    [Function("ChangeExportPolicy")]
    public async Task<IActionResult> Change(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = Route)] HttpRequest request,
        Guid tenantId,
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        var (body, invalid) = await ReadAsync<ExportPolicyChangeRequest>(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await policies.ApplyAsync(tenantId, templateId, body?.Decisions ?? [], auth.Principal!, cancellationToken);
        LogAudit(auth.Principal!, tenantId, templateId, result.Status.ToString(), result.PolicyVersion, result.Changed);

        switch (result.Status)
        {
            case ApplyStatus.Invalid:
                return Json(new ExportPolicyChangeResponse(result, null), StatusCodes.Status400BadRequest);
            case ApplyStatus.UnknownFields:
                return Json(new ExportPolicyChangeResponse(result, null), StatusCodes.Status422UnprocessableEntity);
            case ApplyStatus.Unchanged:
                return Json(new ExportPolicyChangeResponse(result, null), StatusCodes.Status200OK);
        }

        var backfill = await operations.RequestTenantBackfillAsync(tenantId, auth.Principal!, cancellationToken);
        return Json(new ExportPolicyChangeResponse(result, backfill), StatusCodes.Status200OK);
    }

    [Function("GetTenantExportDefault")]
    public Task<IActionResult> GetTenantDefault(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = TenantDefaultRoute)] HttpRequest request,
        Guid tenantId,
        CancellationToken cancellationToken)
        => GetDefaultAsync(request, tenantId, null, cancellationToken);

    [Function("ChangeTenantExportDefault")]
    public Task<IActionResult> ChangeTenantDefault(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = TenantDefaultRoute)] HttpRequest request,
        Guid tenantId,
        CancellationToken cancellationToken)
        => ChangeDefaultAsync(request, tenantId, null, cancellationToken);

    [Function("GetTemplateExportDefault")]
    public Task<IActionResult> GetTemplateDefault(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = TemplateDefaultRoute)] HttpRequest request,
        Guid tenantId,
        Guid templateId,
        CancellationToken cancellationToken)
        => GetDefaultAsync(request, tenantId, templateId, cancellationToken);

    [Function("ChangeTemplateExportDefault")]
    public Task<IActionResult> ChangeTemplateDefault(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = TemplateDefaultRoute)] HttpRequest request,
        Guid tenantId,
        Guid templateId,
        CancellationToken cancellationToken)
        => ChangeDefaultAsync(request, tenantId, templateId, cancellationToken);

    private async Task<IActionResult> GetDefaultAsync(HttpRequest request, Guid tenantId, Guid? templateId, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        return Json(await policies.GetDefaultAsync(tenantId, templateId, cancellationToken), StatusCodes.Status200OK);
    }

    private async Task<IActionResult> ChangeDefaultAsync(HttpRequest request, Guid tenantId, Guid? templateId, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        var (body, invalid) = await ReadAsync<ExportDefaultRequest>(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await policies.SetDefaultAsync(tenantId, templateId, body ?? new ExportDefaultRequest(null, null), auth.Principal!, cancellationToken);
        LogDefaultAudit(auth.Principal!, tenantId, templateId, body?.Mode?.ToString(), result.Status.ToString(), result.PolicyVersion);

        switch (result.Status)
        {
            case ApplyStatus.Invalid:
                return Json(new ExportDefaultChangeResponse(result, null), StatusCodes.Status400BadRequest);
            case ApplyStatus.Unchanged:
                return Json(new ExportDefaultChangeResponse(result, null), StatusCodes.Status200OK);
        }

        var backfill = await operations.RequestTenantBackfillAsync(tenantId, auth.Principal!, cancellationToken);
        return Json(new ExportDefaultChangeResponse(result, backfill), StatusCodes.Status200OK);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Prism admin audit: export policy change by {Principal} for tenant {TenantId} template {TemplateId}: {Result} (version {PolicyVersion}, {Changed} changed)")]
    private partial void LogAudit(string principal, Guid tenantId, Guid templateId, string result, int policyVersion, int changed);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Prism admin audit: export default change to {Mode} by {Principal} for tenant {TenantId} template {TemplateId}: {Result} (version {PolicyVersion})")]
    private partial void LogDefaultAudit(string principal, Guid tenantId, Guid? templateId, string? mode, string result, int policyVersion);
}
