using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LocalAssistant.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.TestDoubles;

public static class TestApiKeyAuthenticationDefaults
{
    public const string SchemeName = "TestApiKey";
    public const string HeaderName = "X-LocalAssistant-Test-Api-Key";
    public const string ScopeClaimType = AuthorizationClaimTypes.Scope;
}

public sealed class TestApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IConfiguration _configuration;
    public TestApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestApiKeyAuthenticationDefaults.HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail("The test API key is invalid."));
        }

        var identity = GetIdentity();
        if (identity is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        var configuredHash = Convert.FromHexString(identity.ApiKeySha256);
        if (!CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash))
        {
            return Task.FromResult(AuthenticateResult.Fail("The test API key is invalid."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.OwnerPrincipalId),
        };
        claims.AddRange(identity.GrantedScopes.Select(
            scope => new Claim(TestApiKeyAuthenticationDefaults.ScopeClaimType, scope)));
        var claimsIdentity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(claimsIdentity), Scheme.Name)));
    }

    private TestIdentity? GetIdentity()
    {
        var principalId = _configuration["LocalAssistant:Identity:PrincipalId"];
        var apiKeyHash = _configuration["LocalAssistant:Identity:ApiKeySha256"];
        var scopes = _configuration.GetSection("LocalAssistant:Identity:Scopes")
            .GetChildren()
            .Select(section => section.Value)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope!)
            .ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(principalId) ||
            string.IsNullOrWhiteSpace(apiKeyHash) ||
            apiKeyHash.Length != 64 ||
            !apiKeyHash.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new TestIdentity(
            principalId,
            apiKeyHash,
            scopes);
    }

    private sealed record TestIdentity(
        string OwnerPrincipalId,
        string ApiKeySha256,
        IReadOnlySet<string> GrantedScopes);
}
