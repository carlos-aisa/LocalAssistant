using System.Net;
using System.Text;
using System.Text.Json;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class OllamaTextEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedAsyncPostsOneNonTruncatedInputToTheLocalEmbedEndpoint()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "embeddings": [[0.25, -0.5]] }""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var embedding = await provider.EmbedAsync("Plan weekly meals.", CancellationToken.None);

        Assert.Equal("embedding-model", embedding.Model);
        Assert.Equal([0.25f, -0.5f], embedding.Values);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://localhost:11434/api/embed", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("embedding-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Plan weekly meals.", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.GetProperty("truncate").GetBoolean());
    }

    [Fact]
    public async Task EmbedAsyncRejectsMissingEmbeddingModelWithoutHttpRequest()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var provider = new OllamaTextEmbeddingProvider(
            client,
            Options.Create(new OllamaOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync("text", CancellationToken.None).AsTask());
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task EmbedAsyncRejectsAResponseWithMoreThanOneEmbedding()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "embeddings": [[0.25], [0.5]] }""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync("text", CancellationToken.None).AsTask());
    }

    private static OllamaTextEmbeddingProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new OllamaOptions
        {
            Endpoint = new Uri("http://localhost:11434"),
            EmbeddingModel = "embedding-model",
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
