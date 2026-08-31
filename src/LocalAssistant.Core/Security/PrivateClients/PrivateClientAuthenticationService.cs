using System.Security.Cryptography;
using System.Text;

namespace LocalAssistant.Core.Security.PrivateClients;

public sealed record AdministrativeChallengeSecret(
    AdministrativeChallenge Challenge,
    string Secret);

public sealed record PrivateClientCredential(
    RegisteredPrivateClient Client,
    string Secret);

public sealed record PrivateClientAccessToken(
    PrivateClientSession Session,
    string Token);

public sealed class PrivateClientAuthenticationService
{
    private const int SecretByteLength = 32;
    private readonly IPrivateClientAuthenticationStore _store;
    private readonly TimeProvider _clock;

    public PrivateClientAuthenticationService(
        IPrivateClientAuthenticationStore store,
        TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public async ValueTask<AdministrativeChallengeSecret> CreateAdministrativeChallengeAsync(
        AdministrativeChallengeOperation operation,
        string? clientId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var now = _clock.GetUtcNow();
        var secret = CreateSecret();
        var challenge = await _store.CreateAdministrativeChallengeAsync(
            operation,
            clientId,
            HashSecret(secret),
            now,
            now.Add(lifetime),
            cancellationToken);
        return new AdministrativeChallengeSecret(challenge, secret);
    }

    public async ValueTask<PrivateClientCredential?> CompleteClientPairingAsync(
        string challengeSecret,
        string ownerPrincipalId,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(challengeSecret) ||
            string.IsNullOrWhiteSpace(ownerPrincipalId) ||
            string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var credential = CreateSecret();
        var client = await _store.ConsumeCreateClientChallengeAsync(
            HashSecret(challengeSecret),
            Guid.NewGuid().ToString("N"),
            ownerPrincipalId,
            displayName.Trim(),
            HashSecret(credential),
            _clock.GetUtcNow(),
            cancellationToken);
        return client is null ? null : new PrivateClientCredential(client, credential);
    }

    public async ValueTask<PrivateClientCredential?> RotateCredentialAsync(
        string challengeSecret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(challengeSecret))
        {
            return null;
        }

        var credential = CreateSecret();
        var client = await _store.ConsumeRotateCredentialChallengeAsync(
            HashSecret(challengeSecret),
            HashSecret(credential),
            _clock.GetUtcNow(),
            cancellationToken);
        return client is null ? null : new PrivateClientCredential(client, credential);
    }

    public ValueTask<bool> RevokeClientAsync(string challengeSecret, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(challengeSecret)
            ? ValueTask.FromResult(false)
            : _store.ConsumeRevokeClientChallengeAsync(
                HashSecret(challengeSecret), _clock.GetUtcNow(), cancellationToken);

    public async ValueTask<PrivateClientAccessToken?> CreateSessionAsync(
        string clientId,
        string credential,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(credential) || lifetime <= TimeSpan.Zero)
        {
            return null;
        }

        var token = CreateSecret();
        var issuedAtUtc = _clock.GetUtcNow();
        var session = await _store.CreateSessionAsync(
            clientId,
            HashSecret(credential),
            HashSecret(token),
            issuedAtUtc,
            issuedAtUtc.Add(lifetime),
            cancellationToken);
        return session is null ? null : new PrivateClientAccessToken(session, token);
    }

    public ValueTask<PrivateClientSession?> FindActiveSessionAsync(
        string accessToken,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(accessToken)
            ? ValueTask.FromResult<PrivateClientSession?>(null)
            : _store.FindActiveSessionAsync(HashSecret(accessToken), _clock.GetUtcNow(), cancellationToken);

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string CreateSecret()
    {
        Span<byte> bytes = stackalloc byte[SecretByteLength];
        RandomNumberGenerator.Fill(bytes);
        var secret = Convert.ToHexString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return secret;
    }
}
