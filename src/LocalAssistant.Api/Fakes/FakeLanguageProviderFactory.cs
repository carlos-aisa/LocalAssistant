using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Api.Fakes;

public sealed class FakeLanguageProviderFactory
{
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });

    public bool TryCreate(string scenario, out ILanguageProvider? provider)
    {
        provider = scenario switch
        {
            "direct" => CreateDirectProvider(),
            "time" => CreateTimeProvider(),
            _ => null,
        };

        return provider is not null;
    }

    private static ScriptedLanguageProvider CreateDirectProvider()
    {
        return new ScriptedLanguageProvider(
        [
            request =>
            {
                var message = request.Messages.Last(item => item.Role == ConversationRole.User).Content;
                return LanguageProviderResponse.Final($"Fake response: {message}");
            },
        ],
        "fake-direct");
    }

    private static ScriptedLanguageProvider CreateTimeProvider()
    {
        return new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(
                new ToolCall("fake-time-call-1", CurrentTimeTool.ToolName, EmptyArguments))),
            request =>
            {
                var result = request.Messages.Last(item => item.ToolResult is not null).ToolResult!;
                using var document = JsonDocument.Parse(result.Content);
                var utc = document.RootElement.GetProperty("utc").GetString();
                return LanguageProviderResponse.Final($"Current UTC time is {utc}.");
            },
        ],
        "fake-time");
    }
}
