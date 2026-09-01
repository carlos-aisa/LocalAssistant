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
    public async Task BootstrapCreatesOneOwnerWithAllCurrentPrivateScopesAndNoApiKey()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);

        var bootstrap = await store.BootstrapAsync(CancellationToken.None);

        Assert.Equal(InstallationBootstrapStatus.Created, bootstrap.Status);
        Assert.NotNull(bootstrap.OwnerPrincipalId);
        var persistedState = await File.ReadAllTextAsync(store.GetStateFilePath(), CancellationToken.None);
        using var persistedDocument = JsonDocument.Parse(persistedState);
        Assert.Equal(4, persistedDocument.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(persistedDocument.RootElement.TryGetProperty("ApiKeySha256", out _));

        var identity = await store.GetAsync(CancellationToken.None);
        Assert.NotNull(identity);
        Assert.Equal(bootstrap.OwnerPrincipalId, identity.OwnerPrincipalId);
        AssertOwnerScopes(identity.GrantedScopes);

        var repeatedBootstrap = await store.BootstrapAsync(CancellationToken.None);
        Assert.Equal(InstallationBootstrapStatus.AlreadyInitialized, repeatedBootstrap.Status);
        Assert.Null(repeatedBootstrap.OwnerPrincipalId);
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
            SchemaVersion = 5,
            InstallationId = "8196f6e9b019487e927ca07f6d3855e9",
            OwnerPrincipalId = "owner-unknown-schema",
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
    public async Task RejectsSchemaFourIdentityThatStillContainsALegacyApiKeyHash()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);
        Directory.CreateDirectory(stateDirectory.Path);
        var stateWithLegacyHash = new
        {
            SchemaVersion = 4,
            InstallationId = "8196f6e9b019487e927ca07f6d3855e9",
            OwnerPrincipalId = "owner-current-schema",
            ApiKeySha256 = CreateApiKeyHash(),
            GrantedScopes = new[] { "installation.owner" },
            InitializedAtUtc = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero),
        };
        await File.WriteAllTextAsync(
            store.GetStateFilePath(),
            JsonSerializer.Serialize(stateWithLegacyHash),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MigratesLegacyIdentityToSchemaFourWithoutChangingStableData(int schemaVersion)
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = CreateStore(stateDirectory.Path);
        Directory.CreateDirectory(stateDirectory.Path);
        var initializedAtUtc = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var legacyState = new
        {
            SchemaVersion = schemaVersion,
            InstallationId = "8196f6e9b019487e927ca07f6d3855e9",
            OwnerPrincipalId = "owner-legacy",
            ApiKeySha256 = CreateApiKeyHash(),
            GrantedScopes = new[] { "installation.owner" },
            InitializedAtUtc = initializedAtUtc,
        };
        await File.WriteAllTextAsync(
            store.GetStateFilePath(),
            JsonSerializer.Serialize(legacyState),
            CancellationToken.None);

        var identity = await store.GetAsync(CancellationToken.None);
        var migratedState = await File.ReadAllTextAsync(store.GetStateFilePath(), CancellationToken.None);
        var secondIdentity = await store.GetAsync(CancellationToken.None);
        var stateAfterSecondRead = await File.ReadAllTextAsync(
            store.GetStateFilePath(),
            CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(legacyState.InstallationId, identity.InstallationId);
        Assert.Equal(legacyState.OwnerPrincipalId, identity.OwnerPrincipalId);
        AssertOwnerScopes(identity.GrantedScopes);
        Assert.NotNull(secondIdentity);
        Assert.Equal(identity.InstallationId, secondIdentity.InstallationId);
        Assert.Equal(identity.OwnerPrincipalId, secondIdentity.OwnerPrincipalId);
        Assert.Equal(
            identity.GrantedScopes.Order(StringComparer.Ordinal),
            secondIdentity.GrantedScopes.Order(StringComparer.Ordinal));
        Assert.Equal(migratedState, stateAfterSecondRead);

        using var migratedDocument = JsonDocument.Parse(migratedState);
        Assert.Equal(4, migratedDocument.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(migratedDocument.RootElement.TryGetProperty("ApiKeySha256", out _));
        Assert.Equal(
            initializedAtUtc,
            migratedDocument.RootElement.GetProperty("InitializedAtUtc").GetDateTimeOffset());
    }

    private static FileInstallationIdentityStore CreateStore(string stateDirectory) => new(
        Options.Create(new InstallationIdentityOptions { StateDirectory = stateDirectory }),
        new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)));

    private static string CreateApiKeyHash() => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("legacy-api-key")));

    private static void AssertOwnerScopes(IReadOnlySet<string> scopes)
    {
        Assert.Equal(11, scopes.Count);
        Assert.Contains("installation.owner", scopes);
        Assert.Contains("memory.personal.read", scopes);
        Assert.Contains("memory.personal.write", scopes);
        Assert.Contains("profile.personal.read", scopes);
        Assert.Contains("profile.personal.write", scopes);
        Assert.Contains("household.profile.read", scopes);
        Assert.Contains("household.profile.write", scopes);
        Assert.Contains("documents.search", scopes);
        Assert.Contains("documents.read", scopes);
        Assert.Contains("documents.content.search", scopes);
        Assert.Contains("reminders.write", scopes);
    }
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
