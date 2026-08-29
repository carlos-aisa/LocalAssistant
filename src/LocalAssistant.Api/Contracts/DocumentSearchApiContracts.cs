using LocalAssistant.Core.Documents;

namespace LocalAssistant.Api.Contracts;

public sealed record SearchDocumentsRequest(
    string? Name = null,
    string? Extension = null,
    string? RelativePath = null,
    DateTimeOffset? ModifiedAfterUtc = null,
    DateTimeOffset? ModifiedBeforeUtc = null,
    int? Limit = null);

public sealed record SearchDocumentContentRequest(
    string Text,
    string? Extension = null,
    string? RelativePath = null,
    DateTimeOffset? ModifiedAfterUtc = null,
    DateTimeOffset? ModifiedBeforeUtc = null,
    int? Limit = null);

public sealed record DocumentSearchResponse(
    IReadOnlyList<DocumentSearchResult> Documents)
{
    public static DocumentSearchResponse FromResults(
        IReadOnlyList<DocumentSearchResult> results) => new(results);
}

public sealed record DocumentContentResponse(
    string Name,
    string Extension,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Text)
{
    public static DocumentContentResponse FromDocument(DocumentContent document)
    {
        return new(
            document.Name,
            document.Extension,
            document.RelativePath,
            document.SizeBytes,
            document.LastModifiedUtc,
            document.Text);
    }
}
