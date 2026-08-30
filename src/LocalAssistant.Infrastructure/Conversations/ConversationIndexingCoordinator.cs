using LocalAssistant.Core.Conversations;

namespace LocalAssistant.Infrastructure.Conversations;

public sealed class ConversationIndexingCoordinator
{
    private readonly SqliteConversationStore _store;
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly IConversationIndexSummaryProvider _summaryProvider;

    public ConversationIndexingCoordinator(
        SqliteConversationStore store,
        ITextEmbeddingProvider embeddingProvider,
        IConversationIndexSummaryProvider summaryProvider)
    {
        _store = store;
        _embeddingProvider = embeddingProvider;
        _summaryProvider = summaryProvider;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var candidates = await _store.ListPendingEmbeddingIndexesAsync(cancellationToken);
        var indexed = 0;
        foreach (var candidate in candidates)
        {
            TextEmbedding? embedding = null;
            if (candidate.RequiresEmbedding)
            {
                embedding = await _embeddingProvider.EmbedAsync(
                    candidate.Text,
                    cancellationToken);
            }

            ConversationIndexSummary? summary = null;
            if (candidate.RequiresSummary)
            {
                try
                {
                    summary = await _summaryProvider.SummarizeAsync(candidate.Text, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                }
            }

            if (await _store.StoreIndexAsync(candidate, embedding, summary, cancellationToken))
            {
                indexed++;
            }
        }

        return indexed;
    }
}
