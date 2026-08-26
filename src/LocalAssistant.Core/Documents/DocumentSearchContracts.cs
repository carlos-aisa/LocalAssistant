namespace LocalAssistant.Core.Documents;

public sealed record DocumentSearchQuery
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 100;

    public DocumentSearchQuery(
        string? name = null,
        string? extension = null,
        string? relativePath = null,
        DateTimeOffset? modifiedAfterUtc = null,
        DateTimeOffset? modifiedBeforeUtc = null,
        int limit = DefaultLimit)
    {
        if (limit is <= 0 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (!string.IsNullOrWhiteSpace(extension) && extension[0] != '.')
        {
            throw new ArgumentException("The extension must start with a period.", nameof(extension));
        }

        if (modifiedAfterUtc is not null &&
            modifiedBeforeUtc is not null &&
            modifiedAfterUtc > modifiedBeforeUtc)
        {
            throw new ArgumentException("The modification date range is invalid.", nameof(modifiedAfterUtc));
        }

        if (!string.IsNullOrWhiteSpace(relativePath) &&
            (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath)))
        {
            throw new ArgumentException("The document path must be relative.", nameof(relativePath));
        }

        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Extension = string.IsNullOrWhiteSpace(extension) ? null : extension.Trim();
        RelativePath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath.Trim();
        ModifiedAfterUtc = modifiedAfterUtc;
        ModifiedBeforeUtc = modifiedBeforeUtc;
        Limit = limit;
    }

    public string? Name { get; }

    public string? Extension { get; }

    public string? RelativePath { get; }

    public DateTimeOffset? ModifiedAfterUtc { get; }

    public DateTimeOffset? ModifiedBeforeUtc { get; }

    public int Limit { get; }
}

public sealed record DocumentSearchResult(
    string Id,
    string Name,
    string Extension,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

public interface ILocalDocumentSearch
{
    ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentSearchQuery query,
        CancellationToken cancellationToken);
}
