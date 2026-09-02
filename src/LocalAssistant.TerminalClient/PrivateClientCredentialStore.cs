using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAssistant.TerminalClient;

public sealed record PrivateClientCredential(string ClientId, string Credential);

public interface IPrivateClientCredentialStore
{
    Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken);

    Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(CancellationToken cancellationToken);
}

public sealed class ManualPrivateClientCredentialStore : IPrivateClientCredentialStore
{
    public Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PrivateClientCredential?>(null);

    public Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> DeleteAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class DpapiPrivateClientCredentialStore : IPrivateClientCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _statePath;

    public DpapiPrivateClientCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAssistant",
            "TerminalClient",
            "private-client.json"))
    {
    }

    public DpapiPrivateClientCredentialStore(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        _statePath = statePath;
    }

    public async Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var stateJson = await File.ReadAllTextAsync(_statePath, cancellationToken);
            var state = JsonSerializer.Deserialize<StoredCredentialState>(stateJson, JsonOptions);
            if (state is null || string.IsNullOrWhiteSpace(state.ClientId) ||
                string.IsNullOrWhiteSpace(state.ProtectedCredential))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(state.ProtectedCredential);
            byte[]? credentialBytes = null;
            try
            {
                credentialBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser);
                var credential = Encoding.UTF8.GetString(credentialBytes);
                return string.IsNullOrWhiteSpace(credential)
                    ? null
                    : new PrivateClientCredential(state.ClientId, credential);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (credentialBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(credentialBytes);
                }
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    public async Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.ClientId) ||
            string.IsNullOrWhiteSpace(credential.Credential) ||
            !OperatingSystem.IsWindows())
        {
            return false;
        }

        byte[]? credentialBytes = null;
        byte[]? protectedBytes = null;
        string? temporaryPath = null;
        try
        {
            credentialBytes = Encoding.UTF8.GetBytes(credential.Credential);
            protectedBytes = ProtectedData.Protect(
                credentialBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            var directory = Path.GetDirectoryName(_statePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
            var state = new StoredCredentialState(credential.ClientId, Convert.ToBase64String(protectedBytes));
            var stateJson = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, stateJson, cancellationToken);
            File.Move(temporaryPath, _statePath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (credentialBytes is not null)
            {
                CryptographicOperations.ZeroMemory(credentialBytes);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<bool> DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
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

    private sealed record StoredCredentialState(string ClientId, string ProtectedCredential);
}
