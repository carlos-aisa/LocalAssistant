namespace LocalAssistant.Core.Profiles;

public sealed record UserProfile(
    string PrincipalId,
    string PreferredName,
    DateTimeOffset UpdatedAtUtc,
    string Source)
{
    public const int MaximumPreferredNameLength = 64;

    public static UserProfile Create(
        string principalId,
        string? preferredName,
        DateTimeOffset updatedAtUtc,
        string? source)
    {
        return new UserProfile(
            ValidatePrincipalId(principalId),
            ValidateText(preferredName, MaximumPreferredNameLength, nameof(preferredName)),
            ValidateTimestamp(updatedAtUtc),
            ValidateText(source, 80, nameof(source)));
    }

    public static string ValidatePrincipalId(string? principalId) =>
        ValidateText(principalId, 128, nameof(principalId));

    public static string ValidateText(string? value, int maximumLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The profile value is invalid.", parameterName);
        }

        return normalized;
    }

    private static DateTimeOffset ValidateTimestamp(DateTimeOffset value) =>
        value == default
            ? throw new ArgumentException("The profile timestamp is invalid.", nameof(value))
            : value;
}

public sealed record HouseholdProfile(
    string Location,
    string TimeZoneId,
    DateTimeOffset UpdatedAtUtc,
    string Source)
{
    public const int MaximumLocationLength = 120;

    public static HouseholdProfile Create(
        string? location,
        string? timeZoneId,
        DateTimeOffset updatedAtUtc,
        string? source)
    {
        var validatedTimeZoneId = UserProfile.ValidateText(
            timeZoneId,
            128,
            nameof(timeZoneId));
        _ = TimeZoneInfo.FindSystemTimeZoneById(validatedTimeZoneId);

        return new HouseholdProfile(
            UserProfile.ValidateText(location, MaximumLocationLength, nameof(location)),
            validatedTimeZoneId,
            updatedAtUtc == default
                ? throw new ArgumentException("The profile timestamp is invalid.", nameof(updatedAtUtc))
                : updatedAtUtc,
            UserProfile.ValidateText(source, 80, nameof(source)));
    }
}

public interface IUserProfileStore
{
    ValueTask<UserProfile?> GetAsync(string principalId, CancellationToken cancellationToken);

    ValueTask<UserProfile> SetPreferredNameAsync(
        string principalId,
        string preferredName,
        string source,
        CancellationToken cancellationToken);
}

public interface IHouseholdProfileStore
{
    ValueTask<HouseholdProfile?> GetAsync(CancellationToken cancellationToken);

    ValueTask<HouseholdProfile> SetLocationAsync(
        string location,
        string timeZoneId,
        string source,
        CancellationToken cancellationToken);
}

public sealed class NullUserProfileStore : IUserProfileStore
{
    public ValueTask<UserProfile?> GetAsync(string principalId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<UserProfile?>(null);
    }

    public ValueTask<UserProfile> SetPreferredNameAsync(
        string principalId,
        string preferredName,
        string source,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<UserProfile>(new InvalidOperationException("User profiles are unavailable."));
}

public sealed class NullHouseholdProfileStore : IHouseholdProfileStore
{
    public ValueTask<HouseholdProfile?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<HouseholdProfile?>(null);
    }

    public ValueTask<HouseholdProfile> SetLocationAsync(
        string location,
        string timeZoneId,
        string source,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<HouseholdProfile>(new InvalidOperationException("Household profiles are unavailable."));
}
