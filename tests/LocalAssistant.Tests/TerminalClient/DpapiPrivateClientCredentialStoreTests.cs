using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class DpapiPrivateClientCredentialStoreTests
{
    [Fact]
    public async Task SavesCredentialsWithDpapiAndLoadsThemBack()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "private-client.json");
            var store = new DpapiPrivateClientCredentialStore(path);
            var credential = new PrivateClientCredential("client-a", "credential-a");

            var saved = await store.SaveAsync(credential, CancellationToken.None);
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(saved);
                Assert.Null(await store.LoadAsync(CancellationToken.None));
                Assert.False(File.Exists(path));
                return;
            }

            var loaded = await store.LoadAsync(CancellationToken.None);
            var storedJson = await File.ReadAllTextAsync(path);

            Assert.True(saved);
            Assert.Equal(credential, loaded);
            Assert.DoesNotContain(credential.Credential, storedJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptStateIsIgnoredSafely()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "private-client.json");
            await File.WriteAllTextAsync(path, "not-json");
            var store = new DpapiPrivateClientCredentialStore(path);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Null(loaded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveFailureDoesNotLeaveAStateFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var blockingPath = Path.Combine(directory, "not-a-directory");
            await File.WriteAllTextAsync(blockingPath, "block");
            var statePath = Path.Combine(blockingPath, "private-client.json");
            var store = new DpapiPrivateClientCredentialStore(statePath);

            var saved = await store.SaveAsync(
                new PrivateClientCredential("client-a", "credential-a"),
                CancellationToken.None);

            Assert.False(saved);
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocalAssistant.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
