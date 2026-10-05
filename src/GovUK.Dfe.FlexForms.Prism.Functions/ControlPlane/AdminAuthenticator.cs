using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
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
/// and audience every request is rejected. A caller that also has the delegate role may name the person it acts
/// for in <see cref="ActingUserHeader"/>; the principal is then recorded as "person via caller".
/// </summary>
public sealed partial class AdminAuthenticator : IAdminAuthenticator
{
    public const string ActingUserHeader = "X-Prism-Acting-User";
    public const string DevelopmentKeyHeader = "X-Prism-Development-Key";
    private const string DevelopmentPrincipal = "development-key";
    private const int MaxPrincipalLength = 256;
    private const int MaxActingUserLength = 160;

    private readonly AdminAuthOptions options;
    private readonly BaseConfigurationManager? configurationManager;
    private readonly byte[]? developmentKey;
    private readonly ILogger<AdminAuthenticator> logger;
    private readonly JsonWebTokenHandler handler = new();

    public AdminAuthenticator(IOptions<PrismFunctionsOptions> options, IHostEnvironment environment, ILogger<AdminAuthenticator> logger)
        : this(options.Value.Admin, CreateConfigurationManager(options.Value.Admin), IsDevelopment(environment), logger)
    {
    }

    internal AdminAuthenticator(
        AdminAuthOptions options,
        BaseConfigurationManager? configurationManager,
        ILogger<AdminAuthenticator> logger)
        : this(options, configurationManager, developmentKeyAllowed: false, logger)
    {
    }

    internal AdminAuthenticator(
        AdminAuthOptions options,
        BaseConfigurationManager? configurationManager,
        bool developmentKeyAllowed,
        ILogger<AdminAuthenticator> logger)
    {
        this.options = options;
        this.configurationManager = configurationManager;
        this.logger = logger;
        if (!string.IsNullOrEmpty(options.DevelopmentKey))
        {
            if (developmentKeyAllowed)
            {
                developmentKey = Encoding.UTF8.GetBytes(options.DevelopmentKey);
            }
            else
            {
                LogDevelopmentKeyIgnored();
            }
        }
    }

    public async Task<AdminAuthResult> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (developmentKey is not null && request.Headers.TryGetValue(DevelopmentKeyHeader, out var presented))
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented.ToString()), developmentKey)
                ? WithActingUser(request, DevelopmentPrincipal, canDelegate: true)
                : new AdminAuthResult(AdminAuthStatus.Unauthenticated);
        }

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
        if (!HasRole(identity, options.RequiredRole))
        {
            LogMissingRole(principal, options.RequiredRole);
            return new AdminAuthResult(AdminAuthStatus.Forbidden);
        }

        return WithActingUser(request, principal, HasRole(identity, options.DelegateRole));
    }

    private AdminAuthResult WithActingUser(HttpRequest request, string caller, bool canDelegate)
    {
        var actingUser = Clean(request.Headers[ActingUserHeader].ToString());
        if (actingUser.Length == 0)
        {
            return new AdminAuthResult(AdminAuthStatus.Authorized, caller);
        }

        if (!canDelegate)
        {
            LogMissingRole(caller, options.DelegateRole);
            return new AdminAuthResult(AdminAuthStatus.Forbidden);
        }

        return new AdminAuthResult(AdminAuthStatus.Authorized, Truncate($"{actingUser} via {caller}", MaxPrincipalLength));
    }

    private static bool HasRole(ClaimsIdentity identity, string role) =>
        !string.IsNullOrEmpty(role) && identity.Claims.Any(c => c.Type == "roles" && c.Value == role);

    private static string Clean(string value) =>
        Truncate(new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim(), MaxActingUserLength);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static string Describe(ClaimsIdentity identity)
    {
        string? Claim(string type) => identity.FindFirst(type)?.Value;

        var name = Claim("preferred_username") ?? Claim("upn") ?? Claim("name") ?? Claim("azp") ?? Claim("appid") ?? Claim("sub") ?? "unknown";
        var objectId = Claim("oid");
        var description = objectId is null ? name : $"{name} ({objectId})";
        return Truncate(description, MaxPrincipalLength);
    }

    private static bool IsDevelopment(IHostEnvironment environment) =>
        environment.IsDevelopment()
        || string.Equals(Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT"), Environments.Development, StringComparison.OrdinalIgnoreCase);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prism:Admin:DevelopmentKey is set but ignored because the environment is not Development")]
    private partial void LogDevelopmentKeyIgnored();
}
