using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAssistant.TerminalClient;

public sealed record PrivateClientCredential(
    string ClientId,
    string Credential,
    Guid? LastConversationId = null);

public interface IPrivateClientCredentialStore
{
    Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken);

    Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(CancellationToken cancellationToken);
}

internal interface IPrivateClientSpokenOutputPreferencesStore
{
    Task<SpokenOutputPreferences> LoadSpokenOutputPreferencesAsync(CancellationToken cancellationToken);

    Task<bool> SaveSpokenOutputPreferencesAsync(
        PrivateClientCredential credential,
        SpokenOutputPreferences preferences,
        CancellationToken cancellationToken);
}

internal interface IPrivateClientStateProtector
{
    bool IsAvailable { get; }

    byte[] Protect(byte[] clearBytes);

    byte[] Unprotect(byte[] protectedBytes);
}

internal interface IPrivateClientStateFileSystem
{
    bool FileExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken);

    void CreateDirectory(string path);

    void ReplaceAtomically(string sourcePath, string destinationPath);

    void DeleteFile(string path);
}

[SupportedOSPlatform("windows")]
internal sealed class DpapiPrivateClientStateProtector : IPrivateClientStateProtector
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] clearBytes) => ProtectedData.Protect(
        clearBytes,
        optionalEntropy: null,
        DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(
        protectedBytes,
        optionalEntropy: null,
        DataProtectionScope.CurrentUser);
}

internal sealed class SystemPrivateClientStateFileSystem : IPrivateClientStateFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void ReplaceAtomically(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(sourcePath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    public void DeleteFile(string path) => File.Delete(path);
}

public sealed class ManualPrivateClientCredentialStore :
    IPrivateClientCredentialStore,
    IPrivateClientSpokenOutputPreferencesStore
{
    public Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PrivateClientCredential?>(null);

    public Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> DeleteAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    Task<SpokenOutputPreferences> IPrivateClientSpokenOutputPreferencesStore.LoadSpokenOutputPreferencesAsync(
        CancellationToken cancellationToken) => Task.FromResult(SpokenOutputPreferences.Default);

    Task<bool> IPrivateClientSpokenOutputPreferencesStore.SaveSpokenOutputPreferencesAsync(
        PrivateClientCredential credential,
        SpokenOutputPreferences preferences,
        CancellationToken cancellationToken) => Task.FromResult(false);
}

public sealed class DpapiPrivateClientCredentialStore :
    IPrivateClientCredentialStore,
    IPrivateClientSpokenOutputPreferencesStore
{
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _statePath;
    private readonly IPrivateClientStateProtector _protector;
    private readonly IPrivateClientStateFileSystem _fileSystem;

    public DpapiPrivateClientCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAssistant",
            "TerminalClient",
            "private-client.json"))
    {
    }

    public DpapiPrivateClientCredentialStore(string statePath)
        : this(
            statePath,
            CreateDefaultProtector(),
            new SystemPrivateClientStateFileSystem())
    {
    }

    internal DpapiPrivateClientCredentialStore(
        string statePath,
        IPrivateClientStateProtector protector,
        IPrivateClientStateFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _statePath = statePath;
    }

    /// <summary>
    /// The local state file path. Not a secret: it is a path on the user's own machine,
    /// already documented, and useful for diagnostics.
    /// </summary>
    internal string StatePath => _statePath;

    /// <summary>
    /// Reads the state file's readable envelope without ever unprotecting the DPAPI
    /// payload, so it never has the credential or the voice preferences stored inside
    /// it in memory. For <c>--diagnostics</c>: schema version, <c>ClientId</c> (an
    /// identifier, not a secret) and whether a last conversation id is present.
    /// </summary>
    internal async Task<TerminalDiagnosticsLocalStateSection> ReadLocalStateSectionAsync(
        CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(_statePath))
        {
            return TerminalDiagnosticsLocalStateSection.Missing(_statePath);
        }

        try
        {
            var stateJson = await _fileSystem.ReadAllTextAsync(_statePath, cancellationToken);
            var state = JsonSerializer.Deserialize<StoredCredentialState>(stateJson, JsonOptions);
            if (state is null || string.IsNullOrWhiteSpace(state.ClientId))
            {
                return new TerminalDiagnosticsLocalStateSection(
                    _statePath,
                    FileExists: true,
                    IsReadable: false,
                    SchemaVersion: state?.SchemaVersion,
                    ClientId: null,
                    HasLastConversationId: false);
            }

            return new TerminalDiagnosticsLocalStateSection(
                _statePath,
                FileExists: true,
                IsReadable: true,
                SchemaVersion: state.SchemaVersion,
                ClientId: state.ClientId,
                HasLastConversationId: state.LastConversationId.HasValue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or FormatException)
        {
            return new TerminalDiagnosticsLocalStateSection(
                _statePath,
                FileExists: true,
                IsReadable: false,
                SchemaVersion: null,
                ClientId: null,
                HasLastConversationId: false);
        }
    }

    public async Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state?.Credential;
    }

    public async Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.ClientId) ||
            string.IsNullOrWhiteSpace(credential.Credential) ||
            !_protector.IsAvailable)
        {
            return false;
        }

        var currentState = await LoadStateAsync(cancellationToken);
        return await SaveStateAsync(
            credential,
            currentState?.Preferences ?? SpokenOutputPreferences.Default,
            cancellationToken);
    }

    public Task<bool> DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_fileSystem.FileExists(_statePath))
            {
                _fileSystem.DeleteFile(_statePath);
            }

            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    async Task<SpokenOutputPreferences> IPrivateClientSpokenOutputPreferencesStore.LoadSpokenOutputPreferencesAsync(
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state?.Preferences ?? SpokenOutputPreferences.Default;
    }

    Task<bool> IPrivateClientSpokenOutputPreferencesStore.SaveSpokenOutputPreferencesAsync(
        PrivateClientCredential credential,
        SpokenOutputPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(preferences);
        return SaveStateAsync(credential, preferences, cancellationToken);
    }

    private async Task<StoredState?> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(_statePath) || !_protector.IsAvailable)
        {
            return null;
        }

        try
        {
            var stateJson = await _fileSystem.ReadAllTextAsync(_statePath, cancellationToken);
            var state = JsonSerializer.Deserialize<StoredCredentialState>(stateJson, JsonOptions);
            if (state is null || string.IsNullOrWhiteSpace(state.ClientId))
            {
                return null;
            }

            if (state.SchemaVersion is null && !string.IsNullOrWhiteSpace(state.ProtectedCredential))
            {
                var credential = UnprotectString(state.ProtectedCredential);
                return string.IsNullOrWhiteSpace(credential)
                    ? null
                    : new StoredState(
                        new PrivateClientCredential(state.ClientId, credential, state.LastConversationId),
                        SpokenOutputPreferences.Default);
            }

            if (state.SchemaVersion != CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(state.ProtectedPayload))
            {
                return null;
            }

            var payloadJson = UnprotectString(state.ProtectedPayload);
            var payload = JsonSerializer.Deserialize<ProtectedCredentialPayload>(payloadJson, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Credential))
            {
                return null;
            }

            var preferences = new SpokenOutputPreferences(
                payload.VoiceId,
                payload.Rate,
                payload.Volume,
                payload.IsMuted);
            return new StoredState(
                new PrivateClientCredential(state.ClientId, payload.Credential, state.LastConversationId),
                preferences);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<bool> SaveStateAsync(
        PrivateClientCredential credential,
        SpokenOutputPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.ClientId) ||
            string.IsNullOrWhiteSpace(credential.Credential) ||
            !_protector.IsAvailable)
        {
            return false;
        }

        byte[]? payloadBytes = null;
        byte[]? protectedBytes = null;
        string? temporaryPath = null;
        try
        {
            var payloadJson = JsonSerializer.Serialize(
                new ProtectedCredentialPayload(
                    credential.Credential,
                    preferences.VoiceId,
                    preferences.Rate,
                    preferences.Volume,
                    preferences.IsMuted),
                JsonOptions);
            payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            protectedBytes = _protector.Protect(payloadBytes);
            var directory = Path.GetDirectoryName(_statePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            _fileSystem.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
            var stateJson = JsonSerializer.Serialize(
                new StoredCredentialState(
                    CurrentSchemaVersion,
                    credential.ClientId,
                    credential.LastConversationId,
                    Convert.ToBase64String(protectedBytes),
                    null),
                JsonOptions);
            await _fileSystem.WriteAllTextAsync(temporaryPath, stateJson, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.ReplaceAtomically(temporaryPath, _statePath);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (payloadBytes is not null)
            {
                CryptographicOperations.ZeroMemory(payloadBytes);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (temporaryPath is not null && _fileSystem.FileExists(temporaryPath))
            {
                try
                {
                    _fileSystem.DeleteFile(temporaryPath);
                }
                catch (IOException)
                {
                    // Cleanup must not replace the safe result of the write operation.
                }
                catch (UnauthorizedAccessException)
                {
                    // Cleanup must not replace the safe result of the write operation.
                }
            }
        }
    }

    private string UnprotectString(string protectedValue)
    {
        var protectedBytes = Convert.FromBase64String(protectedValue);
        byte[]? clearBytes = null;
        try
        {
            clearBytes = _protector.Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }

    private sealed record StoredCredentialState(
        int? SchemaVersion,
        string ClientId,
        Guid? LastConversationId,
        string? ProtectedPayload,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProtectedCredential);

    private sealed record ProtectedCredentialPayload(
        string Credential,
        string? VoiceId,
        int Rate,
        int Volume,
        bool IsMuted);

    private sealed record StoredState(
        PrivateClientCredential Credential,
        SpokenOutputPreferences Preferences);

    private static IPrivateClientStateProtector CreateDefaultProtector() =>
        OperatingSystem.IsWindows()
            ? CreateWindowsProtector()
            : UnavailablePrivateClientStateProtector.Instance;

    [SupportedOSPlatform("windows")]
    private static DpapiPrivateClientStateProtector CreateWindowsProtector() =>
        new DpapiPrivateClientStateProtector();

    private sealed class UnavailablePrivateClientStateProtector : IPrivateClientStateProtector
    {
        public static UnavailablePrivateClientStateProtector Instance { get; } = new();

        public bool IsAvailable => false;

        public byte[] Protect(byte[] clearBytes) => throw new PlatformNotSupportedException();

        public byte[] Unprotect(byte[] protectedBytes) => throw new PlatformNotSupportedException();
    }
}
