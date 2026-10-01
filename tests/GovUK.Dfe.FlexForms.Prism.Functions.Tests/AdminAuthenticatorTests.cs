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
}
