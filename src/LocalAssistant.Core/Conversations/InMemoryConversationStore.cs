using System.Collections.Concurrent;

namespace LocalAssistant.Core.Conversations;

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, ConversationState> _conversations = new();

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
        var state = _conversations.GetOrAdd(conversationId, static _ => new ConversationState());

        lock (state.SyncRoot)
        {
            state.Messages.Add(message);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class ConversationState
    {
        public object SyncRoot { get; } = new();

        public List<ConversationMessage> Messages { get; } = [];
    }
}
