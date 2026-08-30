using LocalAssistant.Core.Conversations;

namespace LocalAssistant.Core.Documents;

public sealed record DocumentSemanticChunk(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    int Position,
    string Text,
    TextEmbedding Embedding)
{
    public const int MaximumExcerptLength = 280;
}

public sealed record IndexedDocument(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string EmbeddingModel);

public sealed record DocumentSemanticChunkInput(
    string Text,
    TextEmbedding Embedding);

public interface IDocumentSemanticIndex
{
    ValueTask ReplaceAsync(
        string relativePath,
        long sizeBytes,
        DateTimeOffset lastModifiedUtc,
        IReadOnlyList<DocumentSemanticChunkInput> chunks,
        CancellationToken cancellationToken);

    ValueTask RemoveAsync(string relativePath, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DocumentSemanticChunk>> GetChunksAsync(
        string embeddingModel,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<IndexedDocument>> GetDocumentsAsync(
        CancellationToken cancellationToken);
}

public static class DocumentTextChunker
{
    public const int MaximumChunkLength = 800;

    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = text.Trim();
        var chunks = new List<string>();

        var offset = 0;
        while (offset < normalized.Length)
        {
            var length = FindChunkLength(normalized, offset);
            chunks.Add(normalized.Substring(offset, length));
            offset += length;

            while (offset < normalized.Length && char.IsWhiteSpace(normalized[offset]))
            {
                offset++;
            }
        }

        return chunks;
    }

    public static string ToExcerpt(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.Length <= DocumentSemanticChunk.MaximumExcerptLength
            ? text
            : text[..DocumentSemanticChunk.MaximumExcerptLength];
    }

    private static int FindChunkLength(string text, int offset)
    {
        var remainingLength = text.Length - offset;

        if (remainingLength <= MaximumChunkLength)
        {
            return remainingLength;
        }

        var maximumEnd = offset + MaximumChunkLength;
        for (var index = maximumEnd; index > offset; index--)
        {
            if (char.IsWhiteSpace(text[index - 1]))
            {
                return index - offset;
            }
        }

        return MaximumChunkLength;
    }
}
