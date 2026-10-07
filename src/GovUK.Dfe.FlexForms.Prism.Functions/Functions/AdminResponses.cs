using System.Text.Json;
using System.Text.Json.Serialization;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Functions;

internal static class AdminResponses
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static IActionResult? Rejected(AdminAuthResult auth) => auth.Status switch
    {
        AdminAuthStatus.Authorized => null,
        AdminAuthStatus.Forbidden => new StatusCodeResult(StatusCodes.Status403Forbidden),
        _ => new UnauthorizedResult(),
    };

    public static JsonResult Json(object value, int statusCode) => new(value, JsonOptions) { StatusCode = statusCode };

    public static JsonResult Problem(int statusCode, string detail) =>
        Json(new ProblemDetails { Status = statusCode, Detail = detail }, statusCode);

    /// <summary>Reads a JSON body, or returns the 400 or 415 response to send instead.</summary>
    public static async Task<(T? Body, IActionResult? Error)> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return (await request.ReadFromJsonAsync<T>(JsonOptions, cancellationToken), null);
        }
        catch (JsonException)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "The request body is not valid JSON."));
        }
        catch (InvalidOperationException)
        {
            return (null, Problem(StatusCodes.Status415UnsupportedMediaType, "Send the request body as application/json."));
        }
    }
}
