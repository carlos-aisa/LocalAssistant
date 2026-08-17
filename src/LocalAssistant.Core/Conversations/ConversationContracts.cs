using System.Text.Json;

namespace LocalAssistant.Core.Conversations;

public enum ConversationRole
{
    User,
    Assistant,
    Tool,
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

public interface IConversationStore
{
    ValueTask<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    ValueTask AppendAsync(
        Guid conversationId,
        ConversationMessage message,
        CancellationToken cancellationToken);
}
