using System.Security.Cryptography;
using GovUK.Dfe.FlexForms.Prism.Functions.ControlPlane;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GovUK.Dfe.FlexForms.Prism.Functions.Tests;

public class AdminAuthenticatorTests
{
    private const string Issuer = "https://login.microsoftonline.com/tenant/v2.0";
    private const string Audience = "api://prism";

    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "k1" };
    private readonly AdminAuthenticator authenticator;

    public AdminAuthenticatorTests()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(key);
        authenticator = new AdminAuthenticator(
            new AdminAuthOptions { Authority = "https://login.microsoftonline.com/tenant/v2.0", Audience = Audience },
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            NullLogger<AdminAuthenticator>.Instance);
    }

    private string Token(string[] roles, string issuer = Issuer, string audience = Audience, SecurityKey? signingKey = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                ["roles"] = roles,
                ["preferred_username"] = "ada@example.org",
                ["oid"] = "1234",
            },
            SigningCredentials = new SigningCredentials(signingKey ?? key, SecurityAlgorithms.RsaSha256),
        });

    private static HttpRequest Request(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context.Request;
    }

    [Fact]
    public async Task A_valid_token_with_the_admin_role_is_authorized_and_names_the_caller()
    {
        var result = await authenticator.AuthenticateAsync(Request($"Bearer {Token(["Prism.Admin"])}"), default);

        Assert.Equal(new AdminAuthResult(AdminAuthStatus.Authorized, "ada@example.org (1234)"), result);
    }

    [Fact]
    public async Task A_valid_token_without_the_role_is_forbidden()
    {
        var result = await authenticator.AuthenticateAsync(Request($"Bearer {Token(["Prism.Read"])}"), default);

        Assert.Equal(AdminAuthStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task Missing_wrong_audience_wrong_issuer_and_foreign_signatures_are_unauthenticated()
    {
        var foreignKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" };

        string?[] headers =
        [
            null,
            "Basic abc",
            $"Bearer {Token(["Prism.Admin"], audience: "api://other")}",
            $"Bearer {Token(["Prism.Admin"], issuer: "https://evil.example/")}",
            $"Bearer {Token(["Prism.Admin"], signingKey: foreignKey)}",
        ];

        foreach (var header in headers)
        {
            Assert.Equal(AdminAuthStatus.Unauthenticated, (await authenticator.AuthenticateAsync(Request(header), default)).Status);
        }
    }

    [Fact]
    public async Task Without_configuration_every_request_is_rejected()
    {
        var unconfigured = new AdminAuthenticator(new AdminAuthOptions(), null, NullLogger<AdminAuthenticator>.Instance);

        var result = await unconfigured.AuthenticateAsync(Request($"Bearer {Token(["Prism.Admin"])}"), default);

        Assert.Equal(AdminAuthStatus.Unauthenticated, result.Status);
    }

    [Fact]
    public async Task A_delegate_caller_is_recorded_as_acting_for_the_named_person()
    {
        var request = Request($"Bearer {Token(["Prism.Admin", "Prism.Delegate"])}");
        request.Headers[AdminAuthenticator.ActingUserHeader] = "jane@school.gov.uk\r\n";

        var result = await authenticator.AuthenticateAsync(request, default);

        Assert.Equal(new AdminAuthResult(AdminAuthStatus.Authorized, "jane@school.gov.uk via ada@example.org (1234)"), result);
    }

    [Fact]
    public async Task Naming_an_acting_person_without_the_delegate_role_is_forbidden()
    {
        var request = Request($"Bearer {Token(["Prism.Admin"])}");
        request.Headers[AdminAuthenticator.ActingUserHeader] = "jane@school.gov.uk";

        Assert.Equal(AdminAuthStatus.Forbidden, (await authenticator.AuthenticateAsync(request, default)).Status);
    }

    [Fact]
    public async Task The_development_key_is_only_accepted_in_development()
    {
        var options = new AdminAuthOptions { DevelopmentKey = "local-key" };
        var development = new AdminAuthenticator(options, null, developmentKeyAllowed: true, NullLogger<AdminAuthenticator>.Instance);
        var production = new AdminAuthenticator(options, null, developmentKeyAllowed: false, NullLogger<AdminAuthenticator>.Instance);

        HttpRequest WithKey(string key)
        {
            var request = Request(null);
            request.Headers[AdminAuthenticator.DevelopmentKeyHeader] = key;
            request.Headers[AdminAuthenticator.ActingUserHeader] = "jane@school.gov.uk";
            return request;
        }

        Assert.Equal(
            new AdminAuthResult(AdminAuthStatus.Authorized, "jane@school.gov.uk via development-key"),
            await development.AuthenticateAsync(WithKey("local-key"), default));
        Assert.Equal(AdminAuthStatus.Unauthenticated, (await development.AuthenticateAsync(WithKey("wrong"), default)).Status);
        Assert.Equal(AdminAuthStatus.Unauthenticated, (await production.AuthenticateAsync(WithKey("local-key"), default)).Status);
    }
}
