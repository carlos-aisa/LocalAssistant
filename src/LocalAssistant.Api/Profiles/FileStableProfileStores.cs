using System.Text.Json;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Profiles;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Profiles;

public sealed class FileStableProfileStores : IUserProfileStore, IHouseholdProfileStore, IDisposable
{
    private const string UserProfilesFileName = "user-profiles.json";
    private const string HouseholdProfileFileName = "household-profile.json";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly InstallationIdentityOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileStableProfileStores(
        IOptions<InstallationIdentityOptions> options,
        TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public async ValueTask<UserProfile?> GetAsync(
        string principalId,
        CancellationToken cancellationToken)
    {
        var validatedPrincipalId = UserProfile.ValidatePrincipalId(principalId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var states = await ReadUserProfilesAsync(cancellationToken);
            return states.TryGetValue(validatedPrincipalId, out var state)
                ? UserProfile.Create(state.PrincipalId, state.PreferredName, state.UpdatedAtUtc, state.Source)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<UserProfile> SetPreferredNameAsync(
        string principalId,
        string preferredName,
        string source,
        CancellationToken cancellationToken)
    {
        var profile = UserProfile.Create(principalId, preferredName, _clock.GetUtcNow(), source);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var states = await ReadUserProfilesAsync(cancellationToken);
            states[profile.PrincipalId] = new(
                profile.PrincipalId,
                profile.PreferredName,
                profile.UpdatedAtUtc,
                profile.Source);
            await WriteAsync(GetStateFilePath(UserProfilesFileName), states.Values.ToArray(), cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HouseholdProfile?> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetStateFilePath(HouseholdProfileFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<StoredHouseholdProfile>(stream, cancellationToken: cancellationToken);
            return state is null
                ? throw new InvalidOperationException("The household profile state is invalid.")
                : HouseholdProfile.Create(state.Location, state.TimeZoneId, state.UpdatedAtUtc, state.Source);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HouseholdProfile> SetLocationAsync(
        string location,
        string timeZoneId,
        string source,
        CancellationToken cancellationToken)
    {
        var profile = HouseholdProfile.Create(location, timeZoneId, _clock.GetUtcNow(), source);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(
                GetStateFilePath(HouseholdProfileFileName),
                new StoredHouseholdProfile(profile.Location, profile.TimeZoneId, profile.UpdatedAtUtc, profile.Source),
                cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Dictionary<string, StoredUserProfile>> ReadUserProfilesAsync(CancellationToken cancellationToken)
    {
        var path = GetStateFilePath(UserProfilesFileName);
        if (!File.Exists(path))
        {
            return new(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(path);
        var states = await JsonSerializer.DeserializeAsync<StoredUserProfile[]>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The user profile state is invalid.");
        var result = new Dictionary<string, StoredUserProfile>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var profile = UserProfile.Create(state.PrincipalId, state.PreferredName, state.UpdatedAtUtc, state.Source);
            if (!result.TryAdd(profile.PrincipalId, state))
            {
                throw new InvalidOperationException("The user profile state is invalid.");
            }
        }

        return result;
    }

    private string GetStateFilePath(string fileName)
    {
        var directory = _options.StateDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant");
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new InvalidOperationException("The installation state directory must be an absolute path.");
        }

        return Path.Combine(Path.GetFullPath(directory), fileName);
    }

    private static async Task WriteAsync<T>(string path, T state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The installation state directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record StoredUserProfile(string PrincipalId, string PreferredName, DateTimeOffset UpdatedAtUtc, string Source);
    private sealed record StoredHouseholdProfile(string Location, string TimeZoneId, DateTimeOffset UpdatedAtUtc, string Source);
}
