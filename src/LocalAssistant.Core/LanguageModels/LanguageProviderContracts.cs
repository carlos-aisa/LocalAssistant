using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Core.LanguageModels;

public sealed record LanguageProviderRequest(
    Guid ConversationId,
    IReadOnlyList<ConversationMessage> Messages,
    IReadOnlyList<ToolDefinition> AvailableTools);

public sealed record LanguageProviderResponse(
    string? Content,
    IReadOnlyList<ToolCall> ToolCalls)
{
    public static LanguageProviderResponse Final(string content) => new(content, []);

    public static LanguageProviderResponse RequestTools(params ToolCall[] toolCalls) =>
        new(null, toolCalls);
}

public interface ILanguageProvider
{
    string Name { get; }

    Task<LanguageProviderResponse> GetResponseAsync(
        LanguageProviderRequest request,
        CancellationToken cancellationToken);
}
