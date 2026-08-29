namespace LocalAssistant.Core.Memory;

public sealed record PersonalMemory(
    Guid Id,
    string OwnerPrincipalId,
    string Text,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed class PersonalMemoryDraft
{
    public const int MaximumTextLength = 2_000;

    public PersonalMemoryDraft(string? text)
    {
        var normalizedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            throw new ArgumentException("A personal memory requires non-empty text.", nameof(text));
        }

        if (normalizedText.Length > MaximumTextLength)
        {
            throw new ArgumentException(
                $"Personal memory text cannot exceed {MaximumTextLength} characters.",
                nameof(text));
        }

        Text = normalizedText;
    }

    public string Text { get; }
}

public sealed class PersonalMemoryListQuery
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 100;

    public PersonalMemoryListQuery(int limit = DefaultLimit)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"The personal memory limit must be between 1 and {MaximumLimit}.");
        }

        Limit = limit;
    }

    public int Limit { get; }
}

public interface IPersonalMemoryStore
{
    ValueTask<PersonalMemory> CreateAsync(
        string ownerPrincipalId,
        PersonalMemoryDraft draft,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PersonalMemory>> ListOwnedAsync(
        string ownerPrincipalId,
        PersonalMemoryListQuery query,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteOwnedAsync(
        Guid memoryId,
        string ownerPrincipalId,
        CancellationToken cancellationToken);
}
