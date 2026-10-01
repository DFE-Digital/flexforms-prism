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

/// <summary>
/// Lets the data owner classify a template's fields. A change starts a backfill of the tenant so existing
/// projections pick up the new policy; until it finishes, the views can still show the previous classification.
/// </summary>
public sealed partial class ExportPolicyApi(
    IAdminAuthenticator authenticator,
    ExportPolicyService policies,
    OperationService operations,
    ILogger<ExportPolicyApi> logger)
{
    private const string Route = "admin/tenants/{tenantId:guid}/templates/{templateId:guid}/export-policy";

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

        var backfill = await operations.CreateAsync(OperationKind.Backfill, tenantId, null, auth.Principal!, cancellationToken);
        return Json(new ExportPolicyChangeResponse(result, backfill), StatusCodes.Status200OK);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Prism admin audit: export policy change by {Principal} for tenant {TenantId} template {TemplateId}: {Result} (version {PolicyVersion}, {Changed} changed)")]
    private partial void LogAudit(string principal, Guid tenantId, Guid templateId, string result, int policyVersion, int changed);
}
