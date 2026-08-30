using LocalAssistant.Core.Conversations;

namespace LocalAssistant.Infrastructure.Conversations;

public sealed class ConversationIndexingCoordinator
{
    private readonly SqliteConversationStore _store;
    private readonly ITextEmbeddingProvider _embeddingProvider;

    public ConversationIndexingCoordinator(
        SqliteConversationStore store,
        ITextEmbeddingProvider embeddingProvider)
    {
        _store = store;
        _embeddingProvider = embeddingProvider;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var candidates = await _store.ListPendingEmbeddingIndexesAsync(cancellationToken);
        var indexed = 0;
        foreach (var candidate in candidates)
        {
            var embedding = await _embeddingProvider.EmbedAsync(
                candidate.Text,
                cancellationToken);
            if (await _store.StoreEmbeddingAsync(candidate, embedding, cancellationToken))
            {
                indexed++;
            }
        }

        return indexed;
    }
}
