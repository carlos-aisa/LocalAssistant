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
