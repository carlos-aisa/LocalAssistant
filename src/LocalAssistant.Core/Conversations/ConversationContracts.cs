using System.Text.Json;

namespace LocalAssistant.Core.Conversations;

public enum ConversationRole
{
    User = 0,
    Assistant = 1,
    Tool = 2,
    System = 3,
}

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolResultMessage(
    string ToolCallId,
    string ToolName,
    string Content,
    bool IsError);

public sealed record ConversationMessage(
    ConversationRole Role,
    string? Content = null,
    ToolCall? ToolCall = null,
    ToolResultMessage? ToolResult = null);

public sealed record ConversationMetadata(
    Guid ConversationId,
    string? OwnerPrincipalId);

public sealed record ConversationSummary(
    Guid ConversationId,
    string Title,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset? IndexingRequestedAtUtc);

public sealed record ConversationDetails(
    Guid ConversationId,
    string Title,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset? IndexingRequestedAtUtc);

public sealed record PublicConversationMessage(
    ConversationRole Role,
    string Content);

public sealed record ConversationPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public interface IConversationStore
{
    ValueTask<ConversationMetadata> GetOrCreateMetadataAsync(
        Guid conversationId,
        string? ownerPrincipalId,
        CancellationToken cancellationToken);

    ValueTask<ConversationMetadata?> GetMetadataAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    ValueTask AppendAsync(
        Guid conversationId,
        ConversationMessage message,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteOwnedAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken);

    ValueTask<ConversationPage<ConversationSummary>> ListOwnedAsync(
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<ConversationDetails?> GetOwnedDetailsAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken);

    ValueTask<ConversationPage<PublicConversationMessage>?> GetOwnedHistoryAsync(
        Guid conversationId,
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);
}
