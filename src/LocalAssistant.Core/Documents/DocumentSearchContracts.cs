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

public sealed record DocumentContent(
    string Name,
    string Extension,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Text);

public enum DocumentContentReadFailure
{
    NotFound,
    UnsupportedFormat,
    TooLarge,
}

public sealed record DocumentContentReadOutcome(
    DocumentContent? Document,
    DocumentContentReadFailure? Failure)
{
    public static DocumentContentReadOutcome Found(DocumentContent document)
    {
        return new(document, null);
    }

    public static DocumentContentReadOutcome Failed(DocumentContentReadFailure failure)
    {
        return new(null, failure);
    }
}

public interface ILocalDocumentSearch
{
    ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentSearchQuery query,
        CancellationToken cancellationToken);
}

public sealed record DocumentContentSearchQuery
{
    public const int MaximumTextLength = 200;

    public DocumentContentSearchQuery(
        string text,
        string? extension = null,
        string? relativePath = null,
        DateTimeOffset? modifiedAfterUtc = null,
        DateTimeOffset? modifiedBeforeUtc = null,
        int limit = DocumentSearchQuery.DefaultLimit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("The search text is required.", nameof(text));
        }

        var trimmedText = text.Trim();
        if (trimmedText.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }

        if (limit is <= 0 or > DocumentSearchQuery.MaximumLimit)
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

        Text = trimmedText;
        Extension = string.IsNullOrWhiteSpace(extension) ? null : extension.Trim();
        RelativePath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath.Trim();
        ModifiedAfterUtc = modifiedAfterUtc;
        ModifiedBeforeUtc = modifiedBeforeUtc;
        Limit = limit;
    }

    public string Text { get; }

    public string? Extension { get; }

    public string? RelativePath { get; }

    public DateTimeOffset? ModifiedAfterUtc { get; }

    public DateTimeOffset? ModifiedBeforeUtc { get; }

    public int Limit { get; }
}

public interface ILocalDocumentContentSearch
{
    ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentContentSearchQuery query,
        CancellationToken cancellationToken);
}

public interface IDocumentReferenceProtector
{
    string Protect(string relativePath);

    bool TryUnprotect(string documentReference, out string relativePath);
}

public interface ILocalDocumentContentReader
{
    ValueTask<DocumentContentReadOutcome> ReadAsync(
        string documentReference,
        CancellationToken cancellationToken);
}
