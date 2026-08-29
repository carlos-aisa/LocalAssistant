using LocalAssistant.Api.Profiles;
using LocalAssistant.Api.Security;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Api;

public sealed class FileAssistantProfileStoreTests
{
    [Fact]
    public async Task MissingProfileReturnsTheDefaultWithoutCreatingState()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        using var store = CreateStore(stateDirectory.Path);

        var profile = await store.GetAsync(CancellationToken.None);

        Assert.Equal("LocalAssistant", profile.DisplayName);
        Assert.False(File.Exists(store.GetStateFilePath()));
    }

    [Fact]
    public async Task SetDisplayNamePersistsTheProfileInTheInstallationStateDirectory()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        using var store = CreateStore(stateDirectory.Path);

        var updated = await store.SetDisplayNameAsync("Jarvis", CancellationToken.None);

        Assert.Equal("Jarvis", updated.DisplayName);
        Assert.True(File.Exists(store.GetStateFilePath()));
        var reloaded = await store.GetAsync(CancellationToken.None);
        Assert.Equal("Jarvis", reloaded.DisplayName);
    }

    [Fact]
    public async Task SetDisplayNamePersistsAcrossStoreInstances()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        using (var writer = CreateStore(stateDirectory.Path))
        {
            await writer.SetDisplayNameAsync("Jarvis", CancellationToken.None);
        }

        using var reader = CreateStore(stateDirectory.Path);
        var profile = await reader.GetAsync(CancellationToken.None);

        Assert.Equal("Jarvis", profile.DisplayName);
    }

    [Fact]
    public async Task RejectsInvalidPersistedProfile()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        using var store = CreateStore(stateDirectory.Path);
        Directory.CreateDirectory(stateDirectory.Path);
        await File.WriteAllTextAsync(
            store.GetStateFilePath(),
            "{ \"displayName\": \"\" }",
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.GetAsync(CancellationToken.None));
    }

    private static FileAssistantProfileStore CreateStore(string stateDirectory) => new(
        Options.Create(new InstallationIdentityOptions { StateDirectory = stateDirectory }));
}
