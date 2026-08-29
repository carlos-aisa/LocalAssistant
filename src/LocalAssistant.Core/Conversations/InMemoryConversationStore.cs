using System.Collections.Concurrent;

namespace LocalAssistant.Core.Conversations;

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, ConversationState> _conversations = new();

    public ValueTask<ConversationMetadata> GetOrCreateMetadataAsync(
        Guid conversationId,
        string? ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _conversations.GetOrAdd(
            conversationId,
            id => new ConversationState(new ConversationMetadata(id, ownerPrincipalId)));
        return ValueTask.FromResult(state.Metadata);
    }

    public ValueTask<ConversationMetadata?> GetMetadataAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ConversationMetadata?>(
            _conversations.TryGetValue(conversationId, out var state) ? state.Metadata : null);
    }

    public ValueTask<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_conversations.TryGetValue(conversationId, out var state))
        {
            return ValueTask.FromResult<IReadOnlyList<ConversationMessage>>([]);
        }

        lock (state.SyncRoot)
        {
            return ValueTask.FromResult<IReadOnlyList<ConversationMessage>>([.. state.Messages]);
        }
    }

    public ValueTask AppendAsync(
        Guid conversationId,
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _conversations.GetOrAdd(
            conversationId,
            static id => new ConversationState(new ConversationMetadata(id, null)));

        lock (state.SyncRoot)
        {
            state.Messages.Add(message);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteOwnedAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_conversations.TryGetValue(conversationId, out var state))
        {
            return ValueTask.FromResult(false);
        }

        lock (state.SyncRoot)
        {
            var isOwner = string.Equals(
                state.Metadata.OwnerPrincipalId,
                ownerPrincipalId,
                StringComparison.Ordinal);
            var deleted = isOwner && _conversations.TryRemove(
                new KeyValuePair<Guid, ConversationState>(conversationId, state));

            return ValueTask.FromResult(deleted);
        }
    }

    private sealed class ConversationState
    {
        public ConversationState(ConversationMetadata metadata)
        {
            Metadata = metadata;
        }

        public ConversationMetadata Metadata { get; }

        public object SyncRoot { get; } = new();

        public List<ConversationMessage> Messages { get; } = [];
    }
}
