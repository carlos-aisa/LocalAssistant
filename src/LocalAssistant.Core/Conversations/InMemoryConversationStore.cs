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

    public ValueTask<ConversationPage<ConversationSummary>> ListOwnedAsync(
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);

        var conversations = _conversations.Values
            .Where(state => string.Equals(
                state.Metadata.OwnerPrincipalId,
                ownerPrincipalId,
                StringComparison.Ordinal))
            .OrderBy(state => state.Metadata.ConversationId)
            .Take(limit)
            .Select(state => new ConversationSummary(
                state.Metadata.ConversationId,
                GetTitle(state.Messages),
                DateTimeOffset.UnixEpoch,
                null))
            .ToArray();
        return ValueTask.FromResult(new ConversationPage<ConversationSummary>(conversations, null));
    }

    public ValueTask<ConversationDetails?> GetOwnedDetailsAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_conversations.TryGetValue(conversationId, out var state) ||
            !string.Equals(state.Metadata.OwnerPrincipalId, ownerPrincipalId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<ConversationDetails?>(null);
        }

        lock (state.SyncRoot)
        {
            return ValueTask.FromResult<ConversationDetails?>(new ConversationDetails(
                conversationId,
                GetTitle(state.Messages),
                DateTimeOffset.UnixEpoch,
                null));
        }
    }

    public ValueTask<ConversationPage<PublicConversationMessage>?> GetOwnedHistoryAsync(
        Guid conversationId,
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_conversations.TryGetValue(conversationId, out var state) ||
            !string.Equals(state.Metadata.OwnerPrincipalId, ownerPrincipalId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<ConversationPage<PublicConversationMessage>?>(null);
        }

        lock (state.SyncRoot)
        {
            var messages = state.Messages
                .Where(message => message.Role is ConversationRole.User or ConversationRole.Assistant)
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .Take(limit)
                .Select(message => new PublicConversationMessage(message.Role, message.Content!))
                .ToArray();
            return ValueTask.FromResult<ConversationPage<PublicConversationMessage>?>(
                new ConversationPage<PublicConversationMessage>(messages, null));
        }
    }

    private static string GetTitle(IEnumerable<ConversationMessage> messages) =>
        messages.FirstOrDefault(message =>
            message.Role == ConversationRole.User &&
            !string.IsNullOrWhiteSpace(message.Content))?.Content?.Trim() ?? "Untitled conversation";

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
