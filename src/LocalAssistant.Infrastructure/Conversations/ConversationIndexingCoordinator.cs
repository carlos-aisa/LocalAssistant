using LocalAssistant.Core.Conversations;
using Microsoft.Extensions.Logging;

namespace LocalAssistant.Infrastructure.Conversations;

public sealed partial class ConversationIndexingCoordinator
{
    private readonly SqliteConversationStore _store;
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly IConversationIndexSummaryProvider _summaryProvider;
    private readonly ILogger<ConversationIndexingCoordinator> _logger;

    public ConversationIndexingCoordinator(
        SqliteConversationStore store,
        ITextEmbeddingProvider embeddingProvider,
        IConversationIndexSummaryProvider summaryProvider,
        ILogger<ConversationIndexingCoordinator> logger)
    {
        _store = store;
        _embeddingProvider = embeddingProvider;
        _summaryProvider = summaryProvider;
        _logger = logger;
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
                catch (Exception exception)
                {
                    ConversationSummaryFailed(_logger, exception);
                }
            }

            if (await _store.StoreIndexAsync(candidate, embedding, summary, cancellationToken))
            {
                indexed++;
            }
        }

        return indexed;
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Conversation summary indexing failed and will be retried.")]
    private static partial void ConversationSummaryFailed(ILogger logger, Exception exception);
}
