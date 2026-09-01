namespace LocalAssistant.Core.Security.PrivateClients;

public enum PrivateClientStatus
{
    Active,
    Revoked,
}

public enum AdministrativeChallengeOperation
{
    CreateClient,
    RotateCredential,
    RevokeClient,
}

public sealed record RegisteredPrivateClient(
    string ClientId,
    string OwnerPrincipalId,
    string DisplayName,
    PrivateClientStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long CredentialVersion);

public sealed record PrivateClientSession(
    string SessionId,
    string ClientId,
    string OwnerPrincipalId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long CredentialVersion);

public sealed record AdministrativeChallenge(
    string ChallengeId,
    AdministrativeChallengeOperation Operation,
    string? ClientId,
    DateTimeOffset ExpiresAtUtc);

public interface IPrivateClientAuthenticationStore
{
    ValueTask<bool> HasClientsAsync(CancellationToken cancellationToken);

    ValueTask<RegisteredPrivateClient?> FindActiveClientAsync(
        string clientId,
        CancellationToken cancellationToken);

    ValueTask<AdministrativeChallenge> CreateAdministrativeChallengeAsync(
        AdministrativeChallengeOperation operation,
        string? clientId,
        string secretHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    ValueTask<RegisteredPrivateClient?> ConsumeCreateClientChallengeAsync(
        string secretHash,
        string clientId,
        string ownerPrincipalId,
        string displayName,
        string credentialHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<RegisteredPrivateClient?> ConsumeRotateCredentialChallengeAsync(
        string secretHash,
        string expectedClientId,
        string credentialHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<RegisteredPrivateClient?> ConsumeRevokeClientChallengeAsync(
        string secretHash,
        string expectedClientId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<PrivateClientSession?> CreateSessionAsync(
        string clientId,
        string credentialHash,
        string accessTokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    ValueTask<PrivateClientSession?> FindActiveSessionAsync(
        string accessTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
