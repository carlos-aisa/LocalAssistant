using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Security;

public sealed class InstallationIdentityOptions
{
    public const string SectionName = "LocalAssistant:Installation";

    public string? StateDirectory { get; set; }
}

public sealed record InstallationIdentity(
    string InstallationId,
    string OwnerPrincipalId,
    IReadOnlySet<string> GrantedScopes);

public enum InstallationBootstrapStatus { Created, AlreadyInitialized }

public sealed record InstallationBootstrapResult(
    InstallationBootstrapStatus Status,
    string? OwnerPrincipalId);

public interface IInstallationIdentityStore
{
    ValueTask<InstallationIdentity?> GetAsync(CancellationToken cancellationToken);

    ValueTask<InstallationBootstrapResult> BootstrapAsync(CancellationToken cancellationToken);
}

public sealed class FileInstallationIdentityStore : IInstallationIdentityStore
{
    private const string StateFileName = "installation-identity.json";
    private const string OwnerScope = "installation.owner";
    private const string PersonalMemoryReadScope = "memory.personal.read";
    private const string PersonalMemoryWriteScope = "memory.personal.write";
    private const string PersonalProfileReadScope = "profile.personal.read";
    private const string PersonalProfileWriteScope = "profile.personal.write";
    private const string HouseholdProfileReadScope = "household.profile.read";
    private const string HouseholdProfileWriteScope = "household.profile.write";
    private const string DocumentSearchScope = "documents.search";
    private const string DocumentReadScope = "documents.read";
    private const string DocumentContentSearchScope = "documents.content.search";
    private const string ReminderWriteScope = "reminders.write";
    private const string ConversationReadScope = "conversations.read";
    private const int LegacySchemaVersion = 1;
    private const int PersonalMemorySchemaVersion = 2;
    private const int PrivateClientSchemaVersion = 3;
    private const int CurrentSchemaVersion = 5;
    private readonly InstallationIdentityOptions _options;
    private readonly TimeProvider _clock;

    public FileInstallationIdentityStore(
        IOptions<InstallationIdentityOptions> options,
        TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public async ValueTask<InstallationIdentity?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stateFilePath = GetStateFilePath();
        if (!File.Exists(stateFilePath))
        {
            return null;
        }

        StoredInstallationIdentity? state;
        await using (var stream = new FileStream(
            stateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            state = await JsonSerializer.DeserializeAsync<StoredInstallationIdentity>(
                stream,
                cancellationToken: cancellationToken);
        }

        var validatedState = Validate(state);
        if (validatedState.SchemaVersion != CurrentSchemaVersion)
        {
            validatedState = MigrateLegacyState(validatedState);
            await WriteStateAsync(
                stateFilePath,
                validatedState,
                overwrite: true,
                cancellationToken);
        }

        return ToIdentity(validatedState);
    }

    public async ValueTask<InstallationBootstrapResult> BootstrapAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stateFilePath = GetStateFilePath();
        if (File.Exists(stateFilePath))
        {
            return new(InstallationBootstrapStatus.AlreadyInitialized, null);
        }

        var stateDirectory = Path.GetDirectoryName(stateFilePath)
            ?? throw new InvalidOperationException("The installation state directory is invalid.");
        Directory.CreateDirectory(stateDirectory);

        var ownerPrincipalId = $"owner-{Guid.NewGuid():N}";
        var state = new StoredInstallationIdentity(
            CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            ownerPrincipalId,
            null,
            OwnerScopes,
            _clock.GetUtcNow());

        try
        {
            await WriteStateAsync(
                stateFilePath,
                state,
                overwrite: false,
                cancellationToken);
            return new(InstallationBootstrapStatus.Created, ownerPrincipalId);
        }
        catch (IOException) when (File.Exists(stateFilePath))
        {
            return new(InstallationBootstrapStatus.AlreadyInitialized, null);
        }
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

    private static StoredInstallationIdentity Validate(StoredInstallationIdentity? state)
    {
        if (state is null ||
            state.SchemaVersion is not (
                LegacySchemaVersion or
                PersonalMemorySchemaVersion or
                PrivateClientSchemaVersion or
                4 or
                CurrentSchemaVersion) ||
            !Guid.TryParseExact(state.InstallationId, "N", out _) ||
            !IsValidPrincipalId(state.OwnerPrincipalId) ||
            (state.SchemaVersion < CurrentSchemaVersion && !IsSha256Hash(state.ApiKeySha256)) ||
            (state.SchemaVersion == CurrentSchemaVersion && state.ApiKeySha256 is not null) ||
            state.GrantedScopes is null || state.GrantedScopes.Length == 0 ||
            state.GrantedScopes.Any(scope => string.IsNullOrWhiteSpace(scope)) ||
            state.GrantedScopes.Distinct(StringComparer.Ordinal).Count() != state.GrantedScopes.Length ||
            state.InitializedAtUtc == default)
        {
            throw new InvalidOperationException("The installation identity state is invalid.");
        }

        return state;
    }

    private static StoredInstallationIdentity MigrateLegacyState(
        StoredInstallationIdentity state) => state with
        {
            SchemaVersion = CurrentSchemaVersion,
            ApiKeySha256 = null,
            GrantedScopes = state.GrantedScopes
            .Append(PersonalMemoryReadScope)
            .Append(PersonalMemoryWriteScope)
            .Append(PersonalProfileReadScope)
            .Append(PersonalProfileWriteScope)
            .Append(HouseholdProfileReadScope)
            .Append(HouseholdProfileWriteScope)
            .Append(DocumentSearchScope)
            .Append(DocumentReadScope)
            .Append(DocumentContentSearchScope)
            .Append(ReminderWriteScope)
            .Append(ConversationReadScope)
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
        };

    private static InstallationIdentity ToIdentity(StoredInstallationIdentity state) => new(
        state.InstallationId,
        state.OwnerPrincipalId,
        new HashSet<string>(state.GrantedScopes, StringComparer.Ordinal));

    private static async Task WriteStateAsync(
        string stateFilePath,
        StoredInstallationIdentity state,
        bool overwrite,
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
            File.Move(temporaryFilePath, stateFilePath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }
        }
    }

    private static bool IsValidPrincipalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private static bool IsSha256Hash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] OwnerScopes =
    [
        OwnerScope,
        PersonalMemoryReadScope,
        PersonalMemoryWriteScope,
        PersonalProfileReadScope,
        PersonalProfileWriteScope,
        HouseholdProfileReadScope,
        HouseholdProfileWriteScope,
        DocumentSearchScope,
        DocumentReadScope,
        DocumentContentSearchScope,
        ReminderWriteScope,
        ConversationReadScope,
    ];

    private sealed record StoredInstallationIdentity(
        int SchemaVersion,
        string InstallationId,
        string OwnerPrincipalId,
        string? ApiKeySha256,
        string[] GrantedScopes,
        DateTimeOffset InitializedAtUtc);
}
