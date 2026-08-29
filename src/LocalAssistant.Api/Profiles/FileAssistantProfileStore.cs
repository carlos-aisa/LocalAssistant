using System.Text.Json;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Profiles;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Profiles;

public sealed class FileAssistantProfileStore : IAssistantProfileStore, IDisposable
{
    private const string StateFileName = "assistant-profile.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly InstallationIdentityOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAssistantProfileStore(IOptions<InstallationIdentityOptions> options)
    {
        _options = options.Value;
    }

    public async ValueTask<AssistantProfile> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stateFilePath = GetStateFilePath();
            if (!File.Exists(stateFilePath))
            {
                return AssistantProfile.Default;
            }

            await using var stream = new FileStream(
                stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<StoredAssistantProfile>(
                stream,
                cancellationToken: cancellationToken);

            if (state is null)
            {
                throw new InvalidOperationException("The assistant profile state is invalid.");
            }

            try
            {
                return AssistantProfile.Create(state.DisplayName);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("The assistant profile state is invalid.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AssistantProfile> SetDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken)
    {
        var profile = AssistantProfile.Create(displayName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stateFilePath = GetStateFilePath();
            var stateDirectory = Path.GetDirectoryName(stateFilePath)
                ?? throw new InvalidOperationException("The installation state directory is invalid.");
            Directory.CreateDirectory(stateDirectory);
            await WriteStateAsync(
                stateFilePath,
                new StoredAssistantProfile(profile.DisplayName),
                cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    public string GetStateFilePath()
    {
        var stateDirectory = _options.StateDirectory;
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAssistant");
        }

        if (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathFullyQualified(stateDirectory))
        {
            throw new InvalidOperationException("The installation state directory must be an absolute path.");
        }

        return Path.Combine(Path.GetFullPath(stateDirectory), StateFileName);
    }

    private static async Task WriteStateAsync(
        string stateFilePath,
        StoredAssistantProfile state,
        CancellationToken cancellationToken)
    {
        var stateDirectory = Path.GetDirectoryName(stateFilePath)
            ?? throw new InvalidOperationException("The installation state directory is invalid.");
        var temporaryFilePath = Path.Combine(
            stateDirectory,
            $".{StateFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(
                temporaryFilePath,
                JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions),
                cancellationToken);
            File.Move(temporaryFilePath, stateFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }
        }
    }

    private sealed record StoredAssistantProfile(string? DisplayName);
}
