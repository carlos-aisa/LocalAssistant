using System.Net;
using System.Text;
using System.Text.Json;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class OllamaConversationIndexSummaryProviderTests
{
    [Fact]
    public async Task SummarizeAsyncPostsJsonOnlyRequestToTheLocalChatEndpoint()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "message": { "content": "{\"topic\":\"Weekly meals\",\"summary\":\"We planned weekday dinners.\",\"keywords\":[\"meals\",\"dinners\"]}" } }""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var summary = await provider.SummarizeAsync(
            "Plan weekly meals.",
            CancellationToken.None);

        Assert.Equal("Weekly meals", summary.Topic);
        Assert.Equal("We planned weekday dinners.", summary.Summary);
        Assert.Equal(["meals", "dinners"], summary.Keywords);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:11434/api/chat", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("chat-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task SummarizeAsyncRejectsAnInvalidSummaryWithoutAcceptingIt()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "message": { "content": "not json" } }""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<JsonException>(
            () => provider.SummarizeAsync("Plan weekly meals.", CancellationToken.None).AsTask());
    }

    private static OllamaConversationIndexSummaryProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new OllamaOptions
        {
            Endpoint = new Uri("http://localhost:11434"),
            Model = "chat-model",
        }));

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
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
            return response;
        }
    }
}
