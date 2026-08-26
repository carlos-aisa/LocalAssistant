using LocalAssistant.Core.Documents;

namespace LocalAssistant.Api.Contracts;

public sealed record SearchDocumentsRequest(
    string? Name = null,
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
