using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class KokoroSpeechClientTests
{
    [Fact]
    public async Task HealthSendsCanonicalBearerAndValidatesTheContract()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"status":"ready","apiVersion":"v1"}"""));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("ready", result.Value?.Status);
        Assert.Equal("Bearer AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", handler.Authorization);
    }

    [Fact]
    public async Task InvalidHealthPayloadDoesNotBecomeAReadyResult()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}")));

        var result = await CreateClient(httpClient).GetHealthAsync(CancellationToken.None);

        Assert.Equal(KokoroClientFailureKind.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task SynthesisRejectsAValidHttpResponseWithInvalidWav()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-wave"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var httpClient = new HttpClient(new RecordingHandler(_ => response));

        var result = await CreateClient(httpClient).SynthesizeAsync(Request, CancellationToken.None);

        Assert.Equal(KokoroClientFailureKind.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Unavailable")]
    [InlineData(HttpStatusCode.GatewayTimeout, "Timeout")]
    public async Task SynthesisMapsSafeHttpFailures(HttpStatusCode status, string expected)
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(status)));

        var result = await CreateClient(httpClient).SynthesizeAsync(Request, CancellationToken.None);

        Assert.Equal(Enum.Parse<KokoroClientFailureKind>(expected), result.Failure);
    }

    [Fact]
    public async Task SynthesisMapsTheDocumentedBusyContractSeparatelyFromServiceUnavailability()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ => Json(
            HttpStatusCode.ServiceUnavailable,
            """{ "code": "service_busy" }""")));

        var result = await CreateClient(httpClient).SynthesizeAsync(Request, CancellationToken.None);

        Assert.Equal(KokoroClientFailureKind.Busy, result.Failure);
    }

    [Fact]
    public async Task MissingSecretDoesNotDispatchARequest()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"status":"ready","apiVersion":"v1"}"""));
        using var httpClient = new HttpClient(handler);
        var client = new KokoroSpeechClient(httpClient, static () => null, Options);

        var result = await client.GetHealthAsync(CancellationToken.None);

        Assert.Equal(KokoroClientFailureKind.NotConfigured, result.Failure);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task VoicesTimesOutInsteadOfHangingWhenTheServiceAcceptsButNeverResponds()
    {
        var handler = new HangingHandler();
        using var httpClient = new HttpClient(handler)
        {
            // Mirrors production: the Kokoro HttpClient itself never times out, so a
            // hung response depends entirely on the client's own per-call bound.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = new KokoroServiceOptions(
            new Uri("http://127.0.0.1:57321/"),
            HealthTimeout: TimeSpan.FromMilliseconds(50),
            SynthesisTimeout: TimeSpan.FromSeconds(18));
        var client = CreateClient(httpClient, options);

        var resultTask = client.GetVoicesAsync(CancellationToken.None);
        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(resultTask, completed);
        Assert.Equal(KokoroClientFailureKind.Timeout, (await resultTask).Failure);
    }

    private static KokoroSpeechRequest Request { get; } = new("Hola", "jarvis-es", "es", 1.0, 100);

    private static KokoroServiceOptions Options { get; } = KokoroServiceOptions.Create(new Uri("http://127.0.0.1:57321/"));

    private static KokoroSpeechClient CreateClient(HttpClient httpClient) => CreateClient(httpClient, Options);

    private static KokoroSpeechClient CreateClient(HttpClient httpClient, KokoroServiceOptions options) =>
        new(httpClient, static () => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), options);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable: the delay above never completes.");
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Authorization = request.Headers.Authorization?.ToString() ??
                request.Headers.GetValues("Authorization").SingleOrDefault();
            return Task.FromResult(responseFactory(request));
        }
    }
}
