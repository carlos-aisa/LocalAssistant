using System.Net.Http.Json;
using System.Text.Json;
using LocalAssistant.Core.Conversations;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.LanguageModels.Ollama;

public sealed class OllamaTextEmbeddingProvider : ITextEmbeddingProvider
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;

    public OllamaTextEmbeddingProvider(
        HttpClient httpClient,
        IOptions<OllamaOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async ValueTask<TextEmbedding> EmbedAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text to embed is required.", nameof(text));
        }

        if (!_options.IsEmbeddingConfigured)
        {
            throw new InvalidOperationException(
                "An Ollama embedding model must be configured.");
        }

        var request = new OllamaEmbedRequest(_options.EmbeddingModel, text, Truncate: false);
        using var response = await _httpClient.PostAsJsonAsync(
            OllamaEndpoint.Create(_options.Endpoint, "api/embed"),
            request,
            SerializerOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(
            SerializerOptions,
            cancellationToken);
        var values = payload?.Embeddings is { Count: 1 }
            ? payload.Embeddings[0]
            : null;
        if (values is null || values.Count == 0)
        {
            throw new InvalidOperationException(
                "Ollama returned an invalid embedding response.");
        }

        return new TextEmbedding(_options.EmbeddingModel, values);
    }

    private sealed record OllamaEmbedRequest(string Model, string Input, bool Truncate);

    private sealed record OllamaEmbedResponse(IReadOnlyList<IReadOnlyList<float>>? Embeddings);
}
