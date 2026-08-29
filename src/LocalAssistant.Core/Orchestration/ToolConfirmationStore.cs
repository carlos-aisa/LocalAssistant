using System.Collections.Concurrent;
using LocalAssistant.Core.Conversations;

namespace LocalAssistant.Core.Orchestration;

public sealed record PendingToolConfirmation(
    Guid ConfirmationId,
    Guid OperationId,
    Guid ConversationId,
    string ProviderName,
    string? PrincipalId,
    ToolCall ToolCall,
    IReadOnlyList<ToolCall> RemainingToolCalls,
    DateTimeOffset ExpiresAtUtc);

public interface IToolConfirmationStore
{
    ValueTask<PendingToolConfirmation?> GetAsync(Guid conversationId, CancellationToken cancellationToken);

    ValueTask CreateAsync(PendingToolConfirmation confirmation, CancellationToken cancellationToken);

    ValueTask<PendingToolConfirmation?> TakeAsync(Guid conversationId, Guid confirmationId, CancellationToken cancellationToken);

    ValueTask<bool> RemoveAsync(Guid conversationId, CancellationToken cancellationToken);
}

public sealed class InMemoryToolConfirmationStore : IToolConfirmationStore
{
    private readonly ConcurrentDictionary<Guid, PendingToolConfirmation> _confirmations = new();

    public ValueTask<PendingToolConfirmation?> GetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_confirmations.TryGetValue(conversationId, out var value) ? value : null);
    }

    public ValueTask CreateAsync(PendingToolConfirmation confirmation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_confirmations.TryAdd(confirmation.ConversationId, confirmation))
        {
            throw new InvalidOperationException("A confirmation is already pending for this conversation.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PendingToolConfirmation?> TakeAsync(Guid conversationId, Guid confirmationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_confirmations.TryGetValue(conversationId, out var value) && value.ConfirmationId == confirmationId &&
            _confirmations.TryRemove(new KeyValuePair<Guid, PendingToolConfirmation>(conversationId, value)))
        {
            return ValueTask.FromResult<PendingToolConfirmation?>(value);
        }

        return ValueTask.FromResult<PendingToolConfirmation?>(null);
    }

    public ValueTask<bool> RemoveAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_confirmations.TryRemove(conversationId, out _));
    }
}
