using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Security.PrivateClients;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Endpoints;

public static class PrivateClientEndpoints
{
    public static void MapPrivateClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/private/sessions", CreateSessionAsync);
        endpoints.MapPost("/api/private/admin/pairings", CompletePairingAsync);
        endpoints.MapPost("/api/private/admin/credential-rotations", RotateCredentialAsync);
        endpoints.MapPost("/api/private/admin/client-revocations", RevokeClientAsync);
    }

    private static async Task<IResult> CreateSessionAsync(
        CreatePrivateSessionRequest request,
        HttpContext context,
        ILoopbackRequestPolicy loopbackRequestPolicy,
        PrivateClientAuthenticationService authentication,
        IOptions<PrivateClientOptions> options,
        CancellationToken cancellationToken)
    {
        if (!loopbackRequestPolicy.IsLoopback(context))
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.Credential))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["Client ID and credential are required."],
            });
        }

        var session = await authentication.CreateSessionAsync(
            request.ClientId,
            request.Credential,
            options.Value.SessionLifetime,
            cancellationToken);
        return session is null
            ? Results.Unauthorized()
            : Results.Ok(new PrivateSessionResponse(session.Token, session.Session.ExpiresAtUtc));
    }

    private static async Task<IResult> CompletePairingAsync(
        CompletePrivateClientPairingRequest request,
        HttpContext context,
        ILoopbackRequestPolicy loopbackRequestPolicy,
        IInstallationIdentityStore installationIdentityStore,
        PrivateClientAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        if (!loopbackRequestPolicy.IsLoopback(context))
        {
            return Results.NotFound();
        }

        var owner = await installationIdentityStore.GetAsync(cancellationToken);
        if (owner is null || string.IsNullOrWhiteSpace(request.Challenge) ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 128)
        {
            return Results.BadRequest();
        }

        var credential = await authentication.CompleteClientPairingAsync(
            request.Challenge,
            owner.OwnerPrincipalId,
            request.DisplayName,
            cancellationToken);
        return credential is null
            ? Results.NotFound()
            : Results.Ok(new PrivateClientCredentialResponse(
                credential.Client.ClientId,
                credential.Client.DisplayName,
                credential.Secret));
    }

    private static async Task<IResult> RotateCredentialAsync(
        ConsumeAdministrativeChallengeRequest request,
        HttpContext context,
        ILoopbackRequestPolicy loopbackRequestPolicy,
        PrivateClientAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        if (!loopbackRequestPolicy.IsLoopback(context))
        {
            return Results.NotFound();
        }

        var credential = await authentication.RotateCredentialAsync(request.Challenge, cancellationToken);
        return credential is null
            ? Results.NotFound()
            : Results.Ok(new PrivateClientCredentialResponse(
                credential.Client.ClientId,
                credential.Client.DisplayName,
                credential.Secret));
    }

    private static async Task<IResult> RevokeClientAsync(
        ConsumeAdministrativeChallengeRequest request,
        HttpContext context,
        ILoopbackRequestPolicy loopbackRequestPolicy,
        PrivateClientAuthenticationService authentication,
        CancellationToken cancellationToken)
    {
        if (!loopbackRequestPolicy.IsLoopback(context))
        {
            return Results.NotFound();
        }

        return await authentication.RevokeClientAsync(request.Challenge, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
