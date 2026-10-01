using System.Text.Json;
using System.Text.Json.Serialization;
using GovUK.Dfe.FlexForms.Prism.Data.Entities;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using GovUK.Dfe.FlexForms.Prism.Source;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Functions;

public sealed record CreateOperationRequest(Guid? TenantId, DateTime? ModifiedSince);

/// <summary>
/// Control-plane endpoints. They only record operations; the operation worker enqueues the Resync messages.
/// Every call needs an Entra token with the admin role and is audit-logged with the caller.
/// </summary>
public sealed partial class AdminApi(IAdminAuthenticator authenticator, OperationService operations, ISourceClient source, ILogger<AdminApi> logger)
{
    private const string BasePath = "/api/admin/backfill";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Function("CreateBackfill")]
    public Task<IActionResult> CreateBackfill(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/backfill")] HttpRequest request,
        CancellationToken cancellationToken)
        => CreateAsync(OperationKind.Backfill, request, cancellationToken);

    [Function("CreateReconciliation")]
    public Task<IActionResult> CreateReconciliation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/reconciliation")] HttpRequest request,
        CancellationToken cancellationToken)
        => CreateAsync(OperationKind.Reconciliation, request, cancellationToken);

    [Function("GetBackfill")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/backfill/{operationId:guid}")] HttpRequest request,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        var operation = await operations.GetAsync(operationId, cancellationToken);
        return operation is null ? new NotFoundResult() : Json(operation, StatusCodes.Status200OK);
    }

    [Function("CancelBackfill")]
    public async Task<IActionResult> Cancel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/backfill/{operationId:guid}/cancel")] HttpRequest request,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        var (outcome, operation) = await operations.CancelAsync(operationId, auth.Principal!, cancellationToken);
        LogAudit("cancel", auth.Principal!, operationId, operation?.TenantId, outcome.ToString());
        return outcome switch
        {
            CancelOutcome.NotFound => new NotFoundResult(),
            CancelOutcome.AlreadyFinished => Json(operation!, StatusCodes.Status409Conflict),
            _ => Json(operation!, StatusCodes.Status200OK),
        };
    }

    private async Task<IActionResult> CreateAsync(OperationKind kind, HttpRequest request, CancellationToken cancellationToken)
    {
        var auth = await authenticator.AuthenticateAsync(request, cancellationToken);
        if (Rejected(auth) is { } rejected)
        {
            return rejected;
        }

        CreateOperationRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<CreateOperationRequest>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The request body is not valid JSON.");
        }
        catch (InvalidOperationException)
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Send the request body as application/json.");
        }

        if (body is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "A JSON body is required; send {} to cover every tenant.");
        }

        if (body.TenantId is { } tenantId)
        {
            try
            {
                var tenants = await source.GetTenantsAsync(cancellationToken);
                if (tenants.All(t => t.TenantId != tenantId))
                {
                    return Problem(StatusCodes.Status400BadRequest, $"Tenant {tenantId} is not a FlexForms tenant.");
                }
            }
            catch (SourceException)
            {
                return Problem(StatusCodes.Status503ServiceUnavailable, "The tenant list could not be read from FlexForms.");
            }
        }

        var operation = await operations.CreateAsync(kind, body.TenantId, body.ModifiedSince, auth.Principal!, cancellationToken);
        LogAudit($"create_{kind}".ToLowerInvariant(), auth.Principal!, operation.OperationId, operation.TenantId, "accepted");

        var result = Json(operation, StatusCodes.Status202Accepted);
        request.HttpContext.Response.Headers.Location = $"{BasePath}/{operation.OperationId}";
        return result;
    }

    private static IActionResult? Rejected(AdminAuthResult auth) => auth.Status switch
    {
        AdminAuthStatus.Authorized => null,
        AdminAuthStatus.Forbidden => new StatusCodeResult(StatusCodes.Status403Forbidden),
        _ => new UnauthorizedResult(),
    };

    private static JsonResult Json(object value, int statusCode) => new(value, JsonOptions) { StatusCode = statusCode };

    private static JsonResult Problem(int statusCode, string detail) =>
        Json(new ProblemDetails { Status = statusCode, Detail = detail }, statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prism admin audit: {Action} by {Principal} on operation {OperationId} (tenant {TenantId}): {Result}")]
    private partial void LogAudit(string action, string principal, Guid operationId, Guid? tenantId, string result);
}
