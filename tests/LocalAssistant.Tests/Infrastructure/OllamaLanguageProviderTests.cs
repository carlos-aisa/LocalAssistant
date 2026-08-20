using System.Net;
using System.Text;
using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class OllamaLanguageProviderTests
{
    [Theory]
    [InlineData(false, 2048)]
    [InlineData(true, 8192)]
    public async Task GetResponseAsyncMapsConversationAndToolsToNonStreamingChatRequest(
        bool think,
        int contextWindow)
    {
        const string responseJson = """
            {
              "message": {
                "role": "assistant",
                "content": "It is 14:30 UTC."
              }
            }
            """;
        using var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, think, contextWindow);
        var request = new LanguageProviderRequest(
            Guid.Parse("7690e337-bd99-46bc-8ec9-4c54b80b48dc"),
            [new ConversationMessage(ConversationRole.User, "What time is it?")],
            [CreateToolDefinition()]);

        var result = await provider.GetResponseAsync(request, CancellationToken.None);

        Assert.Equal("It is 14:30 UTC.", result.Content);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:11434/api/chat", handler.RequestUri?.AbsoluteUri);

        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(think, root.GetProperty("think").GetBoolean());
        Assert.Equal(
            contextWindow,
            root.GetProperty("options").GetProperty("num_ctx").GetInt32());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("What time is it?", message.GetProperty("content").GetString());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("get_current_time", function.GetProperty("name").GetString());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetResponseAsyncMapsToolCallsFromResponse()
    {
        const string responseJson = """
            {
              "message": {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                  {
                    "function": {
                      "name": "get_current_time",
                      "arguments": { "timezone": "UTC" }
                    }
                  }
                ]
              }
            }
            """;
        using var handler = CreateHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var conversationId = Guid.Parse("7690e337-bd99-46bc-8ec9-4c54b80b48dc");
        var request = new LanguageProviderRequest(
            conversationId,
            [new ConversationMessage(ConversationRole.User, "What time is it?")],
            [CreateToolDefinition()]);

        var result = await provider.GetResponseAsync(request, CancellationToken.None);

        Assert.Null(result.Content);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("ollama:7690e337bd9946bc8ec94c54b80b48dc:1:0", call.Id);
        Assert.Equal("get_current_time", call.Name);
        Assert.Equal("UTC", call.Arguments.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task GetResponseAsyncMapsToolHistoryForFollowUpRequest()
    {
        using var handler = CreateHandler("""
            { "message": { "role": "assistant", "content": "Done." } }
            """);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var call = new ToolCall(
            "call-1",
            "get_current_time",
            ParseJson("""{ "timezone": "UTC" }"""));
        var request = new LanguageProviderRequest(
            Guid.NewGuid(),
            [
                new ConversationMessage(ConversationRole.User, "What time is it?"),
                new ConversationMessage(ConversationRole.Assistant, ToolCall: call),
                new ConversationMessage(
                    ConversationRole.Tool,
                    ToolResult: new ToolResultMessage(
                        call.Id,
                        call.Name,
                        "14:30 UTC",
                        IsError: false)),
            ],
            [CreateToolDefinition()]);

        await provider.GetResponseAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        var mappedCall = Assert.Single(messages[1].GetProperty("tool_calls").EnumerateArray());
        Assert.Equal(
            "get_current_time",
            mappedCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("get_current_time", messages[2].GetProperty("tool_name").GetString());
        Assert.Equal("14:30 UTC", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetResponseAsyncPropagatesHttpFailure()
    {
        using var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    """{ "error": "model runner failed" }""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var request = new LanguageProviderRequest(
            Guid.NewGuid(),
            [new ConversationMessage(ConversationRole.User, "Hello")],
            []);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetResponseAsync(request, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task GetResponseAsyncPropagatesCancellationDuringHttpRequest()
    {
        using var handler = new CancellationObservingHandler();
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        using var cancellation = new CancellationTokenSource();
        var request = new LanguageProviderRequest(
            Guid.NewGuid(),
            [new ConversationMessage(ConversationRole.User, "Hello")],
            []);

        var responseTask = provider.GetResponseAsync(request, cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        Assert.True(await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static OllamaLanguageProvider CreateProvider(
        HttpClient httpClient,
        bool think = false,
        int contextWindow = 4096)
    {
        return new OllamaLanguageProvider(
            httpClient,
            Options.Create(new OllamaOptions
            {
                Endpoint = new Uri("http://localhost:11434"),
                Model = "test-model",
                Think = think,
                ContextWindow = contextWindow,
            }));
    }

    private static RecordingHttpMessageHandler CreateHandler(string responseJson)
    {
        return new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
    }

    private static ToolDefinition CreateToolDefinition()
    {
        return new ToolDefinition(
            new ToolMetadata(
                "get_current_time",
                "Returns the current time.",
                ToolRiskProfile.PublicLocalRead),
            ParseJson("""{ "type": "object", "properties": {} }"""));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }

    private sealed class CancellationObservingHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The HTTP request should have been cancelled.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.SetResult(true);
                throw;
            }
        }
    }
}
