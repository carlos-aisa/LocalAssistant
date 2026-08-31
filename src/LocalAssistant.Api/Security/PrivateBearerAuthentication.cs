using System.Security.Claims;
using LocalAssistant.Core.Security.PrivateClients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Security;

public static class PrivateBearerAuthenticationDefaults
{
    public const string SchemeName = "PrivateBearer";
    public const string ClientIdClaimType = "localassistant.client_id";
    public const string SessionIdClaimType = "localassistant.session_id";
}

public sealed class PrivateBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly PrivateClientAuthenticationService _authentication;
    private readonly IInstallationIdentityStore _installationIdentityStore;
    private readonly ILoopbackRequestPolicy _loopbackRequestPolicy;

    public PrivateBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        PrivateClientAuthenticationService authentication,
        IInstallationIdentityStore installationIdentityStore,
        ILoopbackRequestPolicy loopbackRequestPolicy)
        : base(options, logger, encoder)
    {
        _authentication = authentication;
        _installationIdentityStore = installationIdentityStore;
        _loopbackRequestPolicy = loopbackRequestPolicy;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (!_loopbackRequestPolicy.IsLoopback(Context))
        {
            return AuthenticateResult.Fail("Private bearer authentication is limited to loopback requests.");
        }

        var values = Request.Headers.Authorization;
        if (values.Count != 1 || !values[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("The bearer token is invalid.");
        }

        var token = values[0]!["Bearer ".Length..].Trim();
        var session = await _authentication.FindActiveSessionAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("The bearer token is invalid.");
        }

        var installationIdentity = await _installationIdentityStore.GetAsync(Context.RequestAborted);
        if (installationIdentity is null ||
            !StringComparer.Ordinal.Equals(installationIdentity.OwnerPrincipalId, session.OwnerPrincipalId))
        {
            return AuthenticateResult.Fail("The bearer token is invalid.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.OwnerPrincipalId),
            new(PrivateBearerAuthenticationDefaults.ClientIdClaimType, session.ClientId),
            new(PrivateBearerAuthenticationDefaults.SessionIdClaimType, session.SessionId),
        };
        claims.AddRange(installationIdentity.GrantedScopes.Select(
            scope => new Claim(AuthorizationClaimTypes.Scope, scope)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
