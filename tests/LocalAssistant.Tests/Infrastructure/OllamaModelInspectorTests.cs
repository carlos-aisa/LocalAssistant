using System.Net;
using System.Text;
using System.Text.Json;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class OllamaModelInspectorTests
{
    [Fact]
    public async Task ValidateAsyncAcceptsToolModelAndCachesSuccessfulResult()
    {
        using var handler = new RecordingHandler(_ => CreateResponse(
            HttpStatusCode.OK,
            """{ "capabilities": ["completion", "tools", "thinking"] }"""));
        using var httpClient = new HttpClient(handler);
        var inspector = CreateInspector(httpClient);

        var first = await inspector.ValidateAsync(CancellationToken.None);
        var second = await inspector.ValidateAsync(CancellationToken.None);

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Null(first.ErrorMessage);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("http://localhost:11434/api/show", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ValidateAsyncRejectsModelWithoutToolCapability()
    {
        using var handler = new RecordingHandler(_ => CreateResponse(
            HttpStatusCode.OK,
            """{ "capabilities": ["completion"] }"""));
        using var httpClient = new HttpClient(handler);
        var inspector = CreateInspector(httpClient);

        var result = await inspector.ValidateAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(
            "The configured Ollama model 'test-model' does not support tools.",
            result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsyncReportsMissingModel()
    {
        using var handler = new RecordingHandler(_ => CreateResponse(
            HttpStatusCode.NotFound,
            """{ "error": "model not found" }"""));
        using var httpClient = new HttpClient(handler);
        var inspector = CreateInspector(httpClient);

        var result = await inspector.ValidateAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(
            "The configured Ollama model 'test-model' is not installed.",
            result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsyncReportsOllamaHttpFailure()
    {
        using var handler = new RecordingHandler(_ => CreateResponse(
            HttpStatusCode.InternalServerError,
            """{ "error": "runner unavailable" }"""));
        using var httpClient = new HttpClient(handler);
        var inspector = CreateInspector(httpClient);

        var result = await inspector.ValidateAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(
            "Ollama model validation failed with HTTP status 500.",
            result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsyncReportsConnectionFailure()
    {
        using var handler = new RecordingHandler(_ =>
            throw new HttpRequestException("Connection refused."));
        using var httpClient = new HttpClient(handler);
        var inspector = CreateInspector(httpClient);

        var result = await inspector.ValidateAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(
            "Ollama could not be reached to validate the configured model.",
            result.ErrorMessage);
    }

    private static OllamaModelInspector CreateInspector(HttpClient httpClient)
    {
        return new OllamaModelInspector(
            httpClient,
            Options.Create(new OllamaOptions
            {
                Endpoint = new Uri("http://localhost:11434"),
                Model = "test-model",
            }),
            new OllamaModelValidationCache());
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int CallCount { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory(request);
        }
    }
}
