using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.LanguageModels.Ollama;

public sealed record OllamaModelValidation(bool IsValid, string? ErrorMessage = null)
{
    public static OllamaModelValidation Valid { get; } = new(true);

    public static OllamaModelValidation Invalid(string message) => new(false, message);
}

public sealed class OllamaModelValidationCache
{
    private readonly ConcurrentDictionary<string, byte> _validatedModels = new();

    public bool Contains(Uri endpoint, string model) =>
        _validatedModels.ContainsKey(CreateKey(endpoint, model));

    public void Add(Uri endpoint, string model) =>
        _validatedModels.TryAdd(CreateKey(endpoint, model), 0);

    private static string CreateKey(Uri endpoint, string model) =>
        $"{endpoint.AbsoluteUri.TrimEnd('/')}|{model}";
}

public sealed class OllamaModelInspector
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly OllamaModelValidationCache _cache;

    public OllamaModelInspector(
        HttpClient httpClient,
        IOptions<OllamaOptions> options,
        OllamaModelValidationCache cache)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _cache = cache;
    }

    public async Task<OllamaModelValidation> ValidateAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return OllamaModelValidation.Invalid(
                "Ollama requires LocalAssistant:Ollama:Model configuration.");
        }

        Uri endpoint;
        try
        {
            endpoint = OllamaEndpoint.Create(_options.Endpoint, "api/show");
        }
        catch (InvalidOperationException exception)
        {
            return OllamaModelValidation.Invalid(exception.Message);
        }

        if (_cache.Contains(_options.Endpoint, _options.Model))
        {
            return OllamaModelValidation.Valid;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                endpoint,
                new OllamaShowRequest(_options.Model),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return OllamaModelValidation.Invalid(
                "Ollama could not be reached to validate the configured model.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return OllamaModelValidation.Invalid(
                    $"The configured Ollama model '{_options.Model}' is not installed.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return OllamaModelValidation.Invalid(
                    $"Ollama model validation failed with HTTP status {(int)response.StatusCode}.");
            }

            var model = await response.Content.ReadFromJsonAsync<OllamaShowResponse>(
                cancellationToken: cancellationToken);
            if (model?.Capabilities is null ||
                !model.Capabilities.Contains("tools", StringComparer.OrdinalIgnoreCase))
            {
                return OllamaModelValidation.Invalid(
                    $"The configured Ollama model '{_options.Model}' does not support tools.");
            }
        }

        _cache.Add(_options.Endpoint, _options.Model);
        return OllamaModelValidation.Valid;
    }

    private sealed record OllamaShowRequest(string Model);

    private sealed record OllamaShowResponse(
        [property: JsonPropertyName("capabilities")]
        IReadOnlyList<string>? Capabilities);
}

internal static class OllamaEndpoint
{
    public static Uri Create(Uri endpoint, string relativePath)
    {
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The Ollama endpoint must be an absolute HTTP URL.");
        }

        var baseAddress = endpoint.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(new Uri(baseAddress), relativePath);
    }
}
