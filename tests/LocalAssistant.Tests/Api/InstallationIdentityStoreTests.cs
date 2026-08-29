using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAssistant.Api.Security;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Api;

public sealed class InstallationIdentityStoreTests
{
    [Fact]
    public async Task BootstrapCreatesOneOwnerWithoutPersistingTheApiKey()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);

        var bootstrap = await store.BootstrapAsync(CancellationToken.None);

        Assert.Equal(InstallationBootstrapStatus.Created, bootstrap.Status);
        Assert.NotNull(bootstrap.OwnerPrincipalId);
        Assert.NotNull(bootstrap.ApiKey);
        var persistedState = await File.ReadAllTextAsync(store.GetStateFilePath(), CancellationToken.None);
        Assert.DoesNotContain(bootstrap.ApiKey, persistedState, StringComparison.Ordinal);

        var identity = await store.GetAsync(CancellationToken.None);
        Assert.NotNull(identity);
        Assert.Equal(bootstrap.OwnerPrincipalId, identity.OwnerPrincipalId);
        Assert.Contains("installation.owner", identity.GrantedScopes);
        Assert.Contains("memory.personal.read", identity.GrantedScopes);
        Assert.Contains("memory.personal.write", identity.GrantedScopes);
        Assert.Equal(3, identity.GrantedScopes.Count);
        using var persistedDocument = JsonDocument.Parse(persistedState);
        Assert.Equal(2, persistedDocument.RootElement.GetProperty("SchemaVersion").GetInt32());

        var repeatedBootstrap = await store.BootstrapAsync(CancellationToken.None);
        Assert.Equal(InstallationBootstrapStatus.AlreadyInitialized, repeatedBootstrap.Status);
        Assert.Null(repeatedBootstrap.ApiKey);
    }

    [Fact]
    public async Task RejectsInvalidPersistedIdentityState()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);
        Directory.CreateDirectory(stateDirectory.Path);
        await File.WriteAllTextAsync(
            store.GetStateFilePath(),
            "{ \"schemaVersion\": 1 }",
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsUnknownSchemaVersion()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);
        Directory.CreateDirectory(stateDirectory.Path);
        var stateWithUnknownSchema = new
        {
            SchemaVersion = 3,
            InstallationId = "8196f6e9b019487e927ca07f6d3855e9",
            OwnerPrincipalId = "owner-unknown-schema",
            ApiKeySha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("unknown-schema-api-key"))),
            GrantedScopes = new[] { "installation.owner" },
            InitializedAtUtc = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero),
        };
        await File.WriteAllTextAsync(
            store.GetStateFilePath(),
            JsonSerializer.Serialize(stateWithUnknownSchema),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MigratesSchemaOneIdentityWithoutChangingItsExistingData()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);
        Directory.CreateDirectory(stateDirectory.Path);
        var initializedAtUtc = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var apiKeyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("legacy-api-key")));
        var legacyState = new
        {
            SchemaVersion = 1,
            InstallationId = "8196f6e9b019487e927ca07f6d3855e9",
            OwnerPrincipalId = "owner-legacy",
            ApiKeySha256 = apiKeyHash,
            GrantedScopes = new[] { "installation.owner" },
            InitializedAtUtc = initializedAtUtc,
        };
        await File.WriteAllTextAsync(
            store.GetStateFilePath(),
            JsonSerializer.Serialize(legacyState),
            CancellationToken.None);

        var identity = await store.GetAsync(CancellationToken.None);
        var migratedState = await File.ReadAllTextAsync(
            store.GetStateFilePath(),
            CancellationToken.None);
        var secondIdentity = await store.GetAsync(CancellationToken.None);
        var stateAfterSecondRead = await File.ReadAllTextAsync(
            store.GetStateFilePath(),
            CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(legacyState.InstallationId, identity.InstallationId);
        Assert.Equal(legacyState.OwnerPrincipalId, identity.OwnerPrincipalId);
        Assert.Equal(legacyState.ApiKeySha256, identity.ApiKeySha256);
        Assert.Contains("installation.owner", identity.GrantedScopes);
        Assert.Contains("memory.personal.read", identity.GrantedScopes);
        Assert.Contains("memory.personal.write", identity.GrantedScopes);
        Assert.Equal(3, identity.GrantedScopes.Count);
        Assert.NotNull(secondIdentity);
        Assert.Equal(identity.InstallationId, secondIdentity.InstallationId);
        Assert.Equal(identity.OwnerPrincipalId, secondIdentity.OwnerPrincipalId);
        Assert.Equal(identity.ApiKeySha256, secondIdentity.ApiKeySha256);
        Assert.Equal(
            identity.GrantedScopes.Order(StringComparer.Ordinal),
            secondIdentity.GrantedScopes.Order(StringComparer.Ordinal));
        Assert.Equal(migratedState, stateAfterSecondRead);
        using var migratedDocument = JsonDocument.Parse(migratedState);
        Assert.Equal(2, migratedDocument.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(
            initializedAtUtc,
            migratedDocument.RootElement.GetProperty("InitializedAtUtc").GetDateTimeOffset());
    }

    private static FileInstallationIdentityStore CreateStore(string stateDirectory) => new(
        Options.Create(new InstallationIdentityOptions { StateDirectory = stateDirectory }),
        new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)));
}

internal sealed class TemporaryInstallationStateDirectory : IDisposable
{
    public TemporaryInstallationStateDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"LocalAssistant.Tests.{Guid.NewGuid():N}");
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
