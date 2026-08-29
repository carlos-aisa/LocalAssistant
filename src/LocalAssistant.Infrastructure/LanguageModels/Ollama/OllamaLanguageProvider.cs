using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.LanguageModels.Ollama;

public sealed class OllamaLanguageProvider : ILanguageProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;

    public OllamaLanguageProvider(HttpClient httpClient, IOptions<OllamaOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public string Name => "ollama";

    public async Task<LanguageProviderResponse> GetResponseAsync(
        LanguageProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("An Ollama model must be configured.");
        }

        var chatRequest = new OllamaChatRequest(
            _options.Model,
            MapMessages(request.Messages),
            request.AvailableTools.Select(MapTool).ToArray(),
            Stream: false,
            Think: _options.Think,
            new OllamaRuntimeOptions(_options.ContextWindow));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GetChatEndpoint())
        {
            Content = JsonContent.Create(chatRequest, options: SerializerOptions),
        };
        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var chatResponse = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            SerializerOptions,
            cancellationToken);
        if (chatResponse?.Message is null)
        {
            throw new InvalidOperationException("Ollama returned an empty chat response.");
        }

        if (chatResponse.Message.ToolCalls is { Count: > 0 })
        {
            var calls = chatResponse.Message.ToolCalls
                .Select((call, index) => MapToolCall(request, call, index))
                .ToArray();
            return LanguageProviderResponse.RequestTools(calls);
        }

        return LanguageProviderResponse.Final(chatResponse.Message.Content ?? string.Empty);
    }

    private Uri GetChatEndpoint()
    {
        return OllamaEndpoint.Create(_options.Endpoint, "api/chat");
    }

    private static List<OllamaMessage> MapMessages(
        IReadOnlyList<ConversationMessage> messages)
    {
        var mapped = new List<OllamaMessage>(messages.Count);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];

            if (message.Role == ConversationRole.Assistant && message.ToolCall is not null)
            {
                var toolCalls = new List<OllamaToolCall>();
                while (index < messages.Count &&
                       messages[index].Role == ConversationRole.Assistant &&
                       messages[index].ToolCall is not null)
                {
                    var toolCall = messages[index].ToolCall!;
                    toolCalls.Add(new OllamaToolCall(
                        new OllamaFunctionCall(toolCall.Name, toolCall.Arguments)));
                    index++;
                }

                index--;
                mapped.Add(new OllamaMessage("assistant", ToolCalls: toolCalls));
                continue;
            }

            if (message.Role == ConversationRole.Tool && message.ToolResult is not null)
            {
                mapped.Add(new OllamaMessage(
                    "tool",
                    message.ToolResult.Content,
                    ToolName: message.ToolResult.ToolName));
                continue;
            }

            mapped.Add(new OllamaMessage(
                message.Role switch
                {
                    ConversationRole.System => "system",
                    ConversationRole.User => "user",
                    _ => "assistant",
                },
                message.Content ?? string.Empty));
        }

        return mapped;
    }

    private static OllamaTool MapTool(ToolDefinition definition)
    {
        return new OllamaTool(
            "function",
            new OllamaFunctionDefinition(
                definition.Metadata.Name,
                definition.Metadata.Description,
                definition.InputSchema));
    }

    private static ToolCall MapToolCall(
        LanguageProviderRequest request,
        OllamaToolCall toolCall,
        int index)
    {
        if (string.IsNullOrWhiteSpace(toolCall.Function.Name))
        {
            throw new InvalidOperationException("Ollama returned a tool call without a name.");
        }

        var id = $"ollama:{request.ConversationId:N}:{request.Messages.Count}:{index}";
        return new ToolCall(id, toolCall.Function.Name, toolCall.Function.Arguments.Clone());
    }

    private sealed record OllamaChatRequest(
        string Model,
        IReadOnlyList<OllamaMessage> Messages,
        IReadOnlyList<OllamaTool> Tools,
        bool Stream,
        bool Think,
        OllamaRuntimeOptions Options);

    private sealed record OllamaRuntimeOptions(
        [property: JsonPropertyName("num_ctx")]
        int ContextWindow);

    private sealed record OllamaChatResponse(OllamaMessage? Message);

    private sealed record OllamaMessage(
        string Role,
        string? Content = null,
        [property: JsonPropertyName("tool_calls")]
        IReadOnlyList<OllamaToolCall>? ToolCalls = null,
        [property: JsonPropertyName("tool_name")]
        string? ToolName = null);

    private sealed record OllamaTool(string Type, OllamaFunctionDefinition Function);

    private sealed record OllamaFunctionDefinition(
        string Name,
        string Description,
        JsonElement Parameters);

    private sealed record OllamaToolCall(OllamaFunctionCall Function);

    private sealed record OllamaFunctionCall(string Name, JsonElement Arguments);
}
