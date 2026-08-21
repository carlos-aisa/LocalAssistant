using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LocalAssistant.Core.Security.ToolRisk;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Security;

public static class LocalApiKeyAuthenticationDefaults
{
    public const string SchemeName = "LocalApiKey";
    public const string HeaderName = "X-LocalAssistant-Api-Key";
    public const string ScopeClaimType = "scope";
}

public sealed class LocalIdentityOptions
{
    public const string SectionName = "LocalAssistant:Identity";

    public bool Enabled { get; set; }

    public string PrincipalId { get; set; } = "local-owner";

    public string ApiKeySha256 { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = [];
}

public sealed class LocalApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly LocalIdentityOptions _identityOptions;
    private readonly IInstallationIdentityStore _installationIdentityStore;

    public LocalApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        IOptions<LocalIdentityOptions> identityOptions,
        IInstallationIdentityStore installationIdentityStore)
        : base(options, logger, encoder)
    {
        _identityOptions = identityOptions.Value;
        _installationIdentityStore = installationIdentityStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(LocalApiKeyAuthenticationDefaults.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var effectiveIdentity = _identityOptions.Enabled
            ? new InstallationIdentity(
                "configured-local-identity",
                _identityOptions.PrincipalId,
                _identityOptions.ApiKeySha256,
                new HashSet<string>(_identityOptions.Scopes, StringComparer.Ordinal))
            : await _installationIdentityStore.GetAsync(Context.RequestAborted);
        if (effectiveIdentity is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return AuthenticateResult.Fail("The API key is invalid.");
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        var configuredHash = Convert.FromHexString(effectiveIdentity.ApiKeySha256);
        if (!CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash))
        {
            return AuthenticateResult.Fail("The API key is invalid.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, effectiveIdentity.OwnerPrincipalId),
        };
        claims.AddRange(effectiveIdentity.GrantedScopes.Select(
            scope => new Claim(LocalApiKeyAuthenticationDefaults.ScopeClaimType, scope)));
        var claimsIdentity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(claimsIdentity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}

public sealed class HttpContextToolPolicyContextAccessor(
    IHttpContextAccessor httpContextAccessor) : IToolPolicyContextAccessor
{
    public ToolPolicyContext GetCurrent()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return ToolPolicyContext.Anonymous;
        }

        var principalId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return ToolPolicyContext.Anonymous;
        }

        var scopes = new HashSet<string>(
            principal.FindAll(LocalApiKeyAuthenticationDefaults.ScopeClaimType)
                .Select(claim => claim.Value),
            StringComparer.Ordinal);
        return new ToolPolicyContext(principalId, scopes);
    }
}
