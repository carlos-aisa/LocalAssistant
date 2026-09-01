using LocalAssistant.Core.Security.PrivateClients;
using LocalAssistant.Infrastructure.Security.PrivateClients;
using LocalAssistant.Tests.TestDoubles;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class SqlitePrivateClientAuthenticationStoreTests
{
    [Fact]
    public async Task PairingChallengeCreatesOneClientAndCannotBeConsumedTwice()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock);
        var challenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        var first = await service.CompleteClientPairingAsync(
            challenge.Secret, "owner-a", "Terminal", CancellationToken.None);
        var second = await service.CompleteClientPairingAsync(
            challenge.Secret, "owner-a", "Other", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("owner-a", first.Client.OwnerPrincipalId);
        Assert.Equal("Terminal", first.Client.DisplayName);
        Assert.NotEqual(first.Secret, PrivateClientAuthenticationService.HashSecret(first.Secret));
        Assert.DoesNotContain(first.Secret, File.ReadAllText(Path.Combine(directory.Path, "private-clients.db")));
    }

    [Fact]
    public async Task ConcurrentPairingChallengeConsumptionCreatesOnlyOneClient()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock);
        var challenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        var attempts = await Task.WhenAll(
            service.CompleteClientPairingAsync(
                challenge.Secret,
                "owner-a",
                "First",
                CancellationToken.None).AsTask(),
            service.CompleteClientPairingAsync(
                challenge.Secret,
                "owner-a",
                "Second",
                CancellationToken.None).AsTask());

        Assert.Single(attempts, result => result is not null);
    }

    [Fact]
    public async Task CredentialRotationInvalidatesExistingSessions()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock);
        var client = await CreateClientAsync(service);
        var session = await service.CreateSessionAsync(
            client.Client.ClientId, client.Secret, TimeSpan.FromHours(1), CancellationToken.None);
        var rotationChallenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RotateCredential,
            client.Client.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        var rotated = await service.RotateCredentialAsync(
            rotationChallenge.Secret,
            client.Client.ClientId,
            CancellationToken.None);

        Assert.NotNull(session);
        Assert.NotNull(rotated);
        Assert.Equal(2, rotated.Client.CredentialVersion);
        Assert.Null(await service.FindActiveSessionAsync(session.Token, CancellationToken.None));
        Assert.NotNull(await service.CreateSessionAsync(
            client.Client.ClientId, rotated.Secret, TimeSpan.FromHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task TargetMismatchDoesNotConsumeOrApplyCredentialRotation()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock);
        var client = await CreateClientAsync(service);
        var challenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RotateCredential,
            client.Client.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        var mismatch = await service.RotateCredentialAsync(challenge.Secret, "other-client", CancellationToken.None);
        var rotated = await service.RotateCredentialAsync(
            challenge.Secret,
            client.Client.ClientId,
            CancellationToken.None);

        Assert.Null(mismatch);
        Assert.NotNull(rotated);
        Assert.Equal(2, rotated.Client.CredentialVersion);
    }

    [Fact]
    public async Task ExpiredPairingChallengeCannotCreateAClient()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock);
        var challenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));

        var credential = await service.CompleteClientPairingAsync(
            challenge.Secret, "owner-a", "Terminal", CancellationToken.None);

        Assert.Null(credential);
    }

    [Fact]
    public async Task RevocationAndExpirationPreventSessionResolution()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(directory.Path, clock);
        var client = await CreateClientAsync(service);
        var expired = await service.CreateSessionAsync(
            client.Client.ClientId, client.Secret, TimeSpan.FromMinutes(1), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var revocationChallenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RevokeClient,
            client.Client.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(expired);
        Assert.Null(await service.FindActiveSessionAsync(expired.Token, CancellationToken.None));
        Assert.NotNull(await service.RevokeClientAsync(
            revocationChallenge.Secret,
            client.Client.ClientId,
            CancellationToken.None));
        Assert.Null(await service.CreateSessionAsync(
            client.Client.ClientId, client.Secret, TimeSpan.FromHours(1), CancellationToken.None));
    }

    private static PrivateClientAuthenticationService CreateService(string directory, TimeProvider clock) =>
        new(new SqlitePrivateClientAuthenticationStore(Path.Combine(directory, "private-clients.db")), clock);

    private static async Task<PrivateClientCredential> CreateClientAsync(PrivateClientAuthenticationService service)
    {
        var challenge = await service.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        return (await service.CompleteClientPairingAsync(
            challenge.Secret, "owner-a", "Terminal", CancellationToken.None))!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"LocalAssistant.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
