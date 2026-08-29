namespace LocalAssistant.Core.Profiles;

public sealed record AssistantProfile
{
    public const string DefaultDisplayName = "LocalAssistant";
    public const int MaximumDisplayNameLength = 64;

    private AssistantProfile(string displayName)
    {
        DisplayName = displayName;
    }

    public string DisplayName { get; }

    public static AssistantProfile Default { get; } = new(DefaultDisplayName);

    public static AssistantProfile Create(string? displayName)
    {
        var normalizedDisplayName = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDisplayName) ||
            normalizedDisplayName.Length > MaximumDisplayNameLength ||
            normalizedDisplayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The display name must contain between 1 and {MaximumDisplayNameLength} non-control characters.",
                nameof(displayName));
        }

        return new AssistantProfile(normalizedDisplayName);
    }
}

public interface IAssistantProfileStore
{
    ValueTask<AssistantProfile> GetAsync(CancellationToken cancellationToken);

    ValueTask<AssistantProfile> SetDisplayNameAsync(
        string displayName,
        CancellationToken cancellationToken);
}
