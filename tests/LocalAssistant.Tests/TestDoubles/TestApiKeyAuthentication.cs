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
    private readonly IInstallationIdentityStore _installationIdentityStore;

    public TestApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        IConfiguration configuration,
        IInstallationIdentityStore installationIdentityStore)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
        _installationIdentityStore = installationIdentityStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestApiKeyAuthenticationDefaults.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return AuthenticateResult.Fail("The test API key is invalid.");
        }

        var identity = await GetIdentityAsync(Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.NoResult();
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        var configuredHash = Convert.FromHexString(identity.ApiKeySha256);
        if (!CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash))
        {
            return AuthenticateResult.Fail("The test API key is invalid.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.OwnerPrincipalId),
        };
        claims.AddRange(identity.GrantedScopes.Select(
            scope => new Claim(TestApiKeyAuthenticationDefaults.ScopeClaimType, scope)));
        var claimsIdentity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(claimsIdentity), Scheme.Name));
    }

    private async ValueTask<InstallationIdentity?> GetIdentityAsync(CancellationToken cancellationToken)
    {
        if (!bool.TryParse(_configuration["LocalAssistant:Identity:Enabled"], out var enabled) || !enabled)
        {
            return await _installationIdentityStore.GetAsync(cancellationToken);
        }

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

        return new InstallationIdentity(
            "test-configured-identity",
            principalId,
            apiKeyHash,
            scopes);
    }
}
