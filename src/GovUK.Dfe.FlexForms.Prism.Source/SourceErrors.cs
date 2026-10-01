using GovUK.Dfe.FlexForms.Api.Client.Contracts;

namespace GovUK.Dfe.FlexForms.Prism.Source;

/// <summary>
/// Classifies API client failures. Not-found and request errors are permanent; everything else, including
/// authentication failures (usually configuration or token issues that get fixed), is retried.
/// </summary>
internal static class SourceErrors
{
    public static SourceException? Translate(Exception exception, string what, CancellationToken cancellationToken) =>
        exception switch
        {
            SourceException => null,
            ExternalApplicationsException { StatusCode: 404 } api =>
                new SourceNotFoundException($"The source has no {what}.", api),
            ExternalApplicationsException { StatusCode: 400 or 410 or 422 } api =>
                new SourceRejectedException($"The source rejected the request for {what} ({api.StatusCode}).", api.StatusCode, api),
            ExternalApplicationsException { StatusCode: >= 200 and < 300 } api =>
                new SourceRejectedException($"The source returned an unreadable {what} ({api.StatusCode}).", api.StatusCode, api),
            ExternalApplicationsException api =>
                new SourceUnavailableException($"The source failed to return {what} ({api.StatusCode}).", api.StatusCode, api),
            HttpRequestException http =>
                new SourceUnavailableException($"The source could not be reached for {what}.", (int?)http.StatusCode, http),
            TaskCanceledException timeout when !cancellationToken.IsCancellationRequested =>
                new SourceUnavailableException($"The source timed out returning {what}.", null, timeout),
            Newtonsoft.Json.JsonException json =>
                new SourceRejectedException($"The source returned an unreadable {what}.", null, json),
            _ => null,
        };
}
