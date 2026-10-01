using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;

public enum AdminAuthStatus
{
    Authorized,
    Unauthenticated,
    Forbidden
}

/// <param name="Principal">Who called, for the audit trail. Only set when authorized.</param>
public sealed record AdminAuthResult(AdminAuthStatus Status, string? Principal = null);

public interface IAdminAuthenticator
{
    Task<AdminAuthResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Validates an Entra ID bearer token and requires the admin app role. It fails closed: without an authority
/// and audience every request is rejected.
/// </summary>
public sealed partial class AdminAuthenticator : IAdminAuthenticator
{
    private const int MaxPrincipalLength = 256;

    private readonly AdminAuthOptions options;
    private readonly BaseConfigurationManager? configurationManager;
    private readonly ILogger<AdminAuthenticator> logger;
    private readonly JsonWebTokenHandler handler = new();

    public AdminAuthenticator(IOptions<PrismFunctionsOptions> options, ILogger<AdminAuthenticator> logger)
        : this(options.Value.Admin, CreateConfigurationManager(options.Value.Admin), logger)
    {
    }

    internal AdminAuthenticator(AdminAuthOptions options, BaseConfigurationManager? configurationManager, ILogger<AdminAuthenticator> logger)
    {
        this.options = options;
        this.configurationManager = configurationManager;
        this.logger = logger;
    }

    public async Task<AdminAuthResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (configurationManager is null || string.IsNullOrWhiteSpace(options.Audience))
        {
            LogNotConfigured();
            return new AdminAuthResult(AdminAuthStatus.Unauthenticated);
        }

        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new AdminAuthResult(AdminAuthStatus.Unauthenticated);
        }

        var parameters = new TokenValidationParameters
        {
            ConfigurationManager = configurationManager,
            ValidAudience = options.Audience,
            ValidIssuers = options.ValidIssuers.Length == 0 ? null : options.ValidIssuers,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };

        var result = await handler.ValidateTokenAsync(header["Bearer ".Length..].Trim(), parameters);
        if (!result.IsValid)
        {
            LogInvalidToken(result.Exception?.GetType().Name ?? "unknown");
            return new AdminAuthResult(AdminAuthStatus.Unauthenticated);
        }

        var identity = result.ClaimsIdentity;
        var principal = Describe(identity);
        if (!identity.Claims.Any(c => c.Type == "roles" && c.Value == options.RequiredRole))
        {
            LogMissingRole(principal, options.RequiredRole);
            return new AdminAuthResult(AdminAuthStatus.Forbidden);
        }

        return new AdminAuthResult(AdminAuthStatus.Authorized, principal);
    }

    private static string Describe(ClaimsIdentity identity)
    {
        string? Claim(string type) => identity.FindFirst(type)?.Value;

        var name = Claim("preferred_username") ?? Claim("upn") ?? Claim("name") ?? Claim("azp") ?? Claim("appid") ?? Claim("sub") ?? "unknown";
        var objectId = Claim("oid");
        var description = objectId is null ? name : $"{name} ({objectId})";
        return description.Length <= MaxPrincipalLength ? description : description[..MaxPrincipalLength];
    }

    private static ConfigurationManager<OpenIdConnectConfiguration>? CreateConfigurationManager(AdminAuthOptions options)
        => string.IsNullOrWhiteSpace(options.Authority)
            ? null
            : new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin request rejected: admin authentication is not configured")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin request rejected: invalid token ({Failure})")]
    private partial void LogInvalidToken(string failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin request rejected: {Principal} lacks role {Role}")]
    private partial void LogMissingRole(string principal, string role);
}
