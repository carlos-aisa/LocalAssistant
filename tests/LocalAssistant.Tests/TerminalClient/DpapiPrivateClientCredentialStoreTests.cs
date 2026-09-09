using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public async Task VersionedStateProtectsCredentialAndVoicePreferences()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "private-client.json");
            var store = new DpapiPrivateClientCredentialStore(path);
            var preferences = new SpokenOutputPreferences("Microsoft Elvira", 2, 70, true);
            var credential = new PrivateClientCredential("client-a", "credential-a", Guid.NewGuid());

            var saved = await ((IPrivateClientSpokenOutputPreferencesStore)store)
                .SaveSpokenOutputPreferencesAsync(credential, preferences, CancellationToken.None);
            var loadedCredential = await store.LoadAsync(CancellationToken.None);
            var loadedPreferences = await ((IPrivateClientSpokenOutputPreferencesStore)store)
                .LoadSpokenOutputPreferencesAsync(CancellationToken.None);
            var storedJson = await File.ReadAllTextAsync(path);

            Assert.True(saved);
            Assert.Equal(credential, loadedCredential);
            Assert.Equal(preferences, loadedPreferences);
            Assert.Contains("schemaVersion", storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(credential.Credential, storedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(preferences.VoiceId!, storedJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadsThePreviousStateFormatWithoutLastConversationId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "private-client.json");
            var store = new DpapiPrivateClientCredentialStore(path);
            var credentialBytes = Encoding.UTF8.GetBytes("credential-a");
            var protectedCredential = ProtectedData.Protect(
                credentialBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            var legacyState = JsonSerializer.Serialize(new
            {
                clientId = "client-a",
                protectedCredential = Convert.ToBase64String(protectedCredential),
            });
            await File.WriteAllTextAsync(path, legacyState);
            CryptographicOperations.ZeroMemory(credentialBytes);
            CryptographicOperations.ZeroMemory(protectedCredential);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(new PrivateClientCredential("client-a", "credential-a"), loaded);
            Assert.True(await store.SaveAsync(loaded!, CancellationToken.None));
            using var migratedState = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            Assert.Equal(2, migratedState.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(migratedState.RootElement.TryGetProperty("protectedCredential", out _));
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

    [Fact]
    public async Task FailedReplacementPreservesTheExistingCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "private-client.json");
            var store = new DpapiPrivateClientCredentialStore(path);
            var original = new PrivateClientCredential("client-a", "original-credential");
            Assert.True(await store.SaveAsync(original, CancellationToken.None));

            await using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var saved = await store.SaveAsync(
                    new PrivateClientCredential("client-a", "replacement-credential"),
                    CancellationToken.None);

                Assert.False(saved);
            }

            Assert.Equal(original, await store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PortableReplacementFailurePreservesThePreviousStateByteForByte()
    {
        var fileSystem = new InMemoryPrivateClientStateFileSystem();
        var protector = new ReversiblePrivateClientStateProtector();
        const string path = "C:\\state\\private-client.json";
        var store = new DpapiPrivateClientCredentialStore(path, protector, fileSystem);
        var original = new PrivateClientCredential("client-a", "original-credential", Guid.NewGuid());
        Assert.True(await store.SaveAsync(original, CancellationToken.None));
        var originalBytes = fileSystem.GetFile(path);

        fileSystem.FailReplacement = true;

        var saved = await store.SaveAsync(
            new PrivateClientCredential("client-a", "replacement-credential", original.LastConversationId),
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(originalBytes, fileSystem.GetFile(path));
        Assert.Empty(fileSystem.TemporaryPaths);
        Assert.Equal(original, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeReplacementPreservesThePreviousStateAndCleansTheTemporaryFile()
    {
        var fileSystem = new InMemoryPrivateClientStateFileSystem();
        var protector = new ReversiblePrivateClientStateProtector();
        const string path = "C:\\state\\private-client.json";
        var store = new DpapiPrivateClientCredentialStore(path, protector, fileSystem);
        var original = new PrivateClientCredential("client-a", "original-credential");
        Assert.True(await store.SaveAsync(original, CancellationToken.None));
        var originalBytes = fileSystem.GetFile(path);
        using var cancellation = new CancellationTokenSource();
        fileSystem.AfterWrite = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            new PrivateClientCredential("client-a", "replacement-credential"),
            cancellation.Token));

        Assert.Equal(originalBytes, fileSystem.GetFile(path));
        Assert.Empty(fileSystem.TemporaryPaths);
    }

    [Fact]
    public async Task MigrationFromThePreviousFormatPreservesANonNullLastConversationId()
    {
        var fileSystem = new InMemoryPrivateClientStateFileSystem();
        var protector = new ReversiblePrivateClientStateProtector();
        const string path = "C:\\state\\private-client.json";
        var conversationId = Guid.Parse("6f3a2b1c-8d4e-4f5a-9b6c-7d8e9f0a1b2c");
        var legacyState = JsonSerializer.Serialize(new
        {
            clientId = "client-a",
            protectedCredential = Convert.ToBase64String(
                protector.Protect(Encoding.UTF8.GetBytes("credential-a"))),
            lastConversationId = conversationId,
        });
        fileSystem.SetFile(path, legacyState);
        var store = new DpapiPrivateClientCredentialStore(path, protector, fileSystem);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(new PrivateClientCredential("client-a", "credential-a", conversationId), loaded);

        Assert.True(await store.SaveAsync(loaded!, CancellationToken.None));
        var migrated = await store.LoadAsync(CancellationToken.None);
        var preferences = await ((IPrivateClientSpokenOutputPreferencesStore)store)
            .LoadSpokenOutputPreferencesAsync(CancellationToken.None);
        using var migratedJson = JsonDocument.Parse(fileSystem.GetFile(path));

        Assert.Equal(new PrivateClientCredential("client-a", "credential-a", conversationId), migrated);
        Assert.Equal(SpokenOutputPreferences.Default, preferences);
        Assert.Equal(2, migratedJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(migratedJson.RootElement.TryGetProperty("protectedCredential", out _));

        // Idempotent: a second round-trip over the already-migrated file changes nothing.
        Assert.True(await store.SaveAsync(migrated!, CancellationToken.None));
        Assert.Equal(migrated, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUndecryptablePayloadIsIgnoredWithoutDeletingTheFile()
    {
        var fileSystem = new InMemoryPrivateClientStateFileSystem();
        const string path = "C:\\state\\private-client.json";
        var writable = new ReversiblePrivateClientStateProtector();
        var store = new DpapiPrivateClientCredentialStore(path, writable, fileSystem);
        Assert.True(await store.SaveAsync(new PrivateClientCredential("client-a", "credential-a"), CancellationToken.None));
        var originalBytes = fileSystem.GetFile(path);

        var throwing = new ThrowingPrivateClientStateProtector();
        var reader = new DpapiPrivateClientCredentialStore(path, throwing, fileSystem);

        Assert.Null(await reader.LoadAsync(CancellationToken.None));
        Assert.Equal(originalBytes, fileSystem.GetFile(path));
    }

    [Fact]
    public async Task AWriteThatIsDeniedLeavesTheExistingStateAndReturnsFalse()
    {
        var fileSystem = new InMemoryPrivateClientStateFileSystem();
        var protector = new ReversiblePrivateClientStateProtector();
        const string path = "C:\\state\\private-client.json";
        var store = new DpapiPrivateClientCredentialStore(path, protector, fileSystem);
        var original = new PrivateClientCredential("client-a", "original-credential");
        Assert.True(await store.SaveAsync(original, CancellationToken.None));
        var originalBytes = fileSystem.GetFile(path);
        fileSystem.WriteException = () => new UnauthorizedAccessException("denied");

        var saved = await store.SaveAsync(new PrivateClientCredential("client-a", "replacement"), CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(originalBytes, fileSystem.GetFile(path));
        Assert.Empty(fileSystem.TemporaryPaths);
        Assert.Equal(original, await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownSchemaVersionIsIgnoredSafely()
    {
        var fileSystem = new InMemoryPrivateClientStateFileSystem();
        var protector = new ReversiblePrivateClientStateProtector();
        const string path = "C:\\state\\private-client.json";
        var futureState = JsonSerializer.Serialize(new
        {
            schemaVersion = 99,
            clientId = "client-a",
            protectedPayload = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes("{}"))),
            lastConversationId = (Guid?)null,
        });
        fileSystem.SetFile(path, futureState);
        var store = new DpapiPrivateClientCredentialStore(path, protector, fileSystem);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.Equal(
            SpokenOutputPreferences.Default,
            await ((IPrivateClientSpokenOutputPreferencesStore)store)
                .LoadSpokenOutputPreferencesAsync(CancellationToken.None));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocalAssistant.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ReversiblePrivateClientStateProtector : IPrivateClientStateProtector
    {
        public bool IsAvailable => true;

        public byte[] Protect(byte[] clearBytes) => clearBytes.Reverse().ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.Reverse().ToArray();
    }

    private sealed class ThrowingPrivateClientStateProtector : IPrivateClientStateProtector
    {
        public bool IsAvailable => true;

        public byte[] Protect(byte[] clearBytes) => throw new CryptographicException("cannot protect");

        public byte[] Unprotect(byte[] protectedBytes) => throw new CryptographicException("cannot unprotect");
    }

    private sealed class InMemoryPrivateClientStateFileSystem : IPrivateClientStateFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public bool FailReplacement { get; set; }

        public Func<Exception>? WriteException { get; set; }

        public Action? AfterWrite { get; set; }

        public IReadOnlyCollection<string> TemporaryPaths => _files.Keys
            .Where(path => path.EndsWith(".tmp", StringComparison.Ordinal))
            .ToArray();

        public bool FileExists(string path) => _files.ContainsKey(path);

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_files[path]);
        }

        public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteException is not null)
            {
                throw WriteException();
            }

            _files[path] = contents;
            AfterWrite?.Invoke();
            return Task.CompletedTask;
        }

        public void CreateDirectory(string path)
        {
        }

        public void ReplaceAtomically(string sourcePath, string destinationPath)
        {
            if (FailReplacement)
            {
                throw new IOException("Replacement failed.");
            }

            _files[destinationPath] = _files[sourcePath];
            _files.Remove(sourcePath);
        }

        public void DeleteFile(string path) => _files.Remove(path);

        public string GetFile(string path) => _files[path];

        public void SetFile(string path, string contents) => _files[path] = contents;
    }
}
