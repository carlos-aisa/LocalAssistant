using LocalAssistant.Core.Conversations;

namespace LocalAssistant.Infrastructure.Conversations;

public sealed class HybridConversationContextRetriever : IConversationContextRetriever
{
    private readonly SqliteConversationStore _store;
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly ConversationRetrievalOptions _options;

    public HybridConversationContextRetriever(
        SqliteConversationStore store,
        ITextEmbeddingProvider embeddingProvider,
        ConversationRetrievalOptions options)
    {
        _store = store;
        _embeddingProvider = embeddingProvider;
        _options = options;
    }

    public async ValueTask<ConversationRetrievalResult> RetrieveAsync(
        string ownerPrincipalId,
        Guid currentConversationId,
        string message,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return ConversationRetrievalResult.Empty;
        }

        var literal = await _store.RetrieveAsync(
            ownerPrincipalId,
            currentConversationId,
            message,
            cancellationToken);
        try
        {
            var embedding = await _embeddingProvider.EmbedAsync(
                message,
                cancellationToken);
            var semantic = await _store.RetrieveByEmbeddingAsync(
                ownerPrincipalId,
                currentConversationId,
                embedding,
                cancellationToken);
            return Combine(literal.Matches, semantic);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return literal;
        }
    }

    private ConversationRetrievalResult Combine(
        IReadOnlyList<ConversationRetrievedContext> literal,
        IReadOnlyList<ConversationRetrievedContext> semantic)
    {
        var matches = literal
            .Concat(semantic)
            .GroupBy(match => match.ConversationId)
            .Select(group => group.OrderByDescending(match => match.Score).First())
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.LastActivityUtc)
            .Take(_options.MaximumMatches)
            .ToArray();
        return matches.Length == 0
            ? ConversationRetrievalResult.Empty
            : new ConversationRetrievalResult(matches);
    }
}
