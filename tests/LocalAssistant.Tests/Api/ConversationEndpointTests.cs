using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAssistant.Tests.Api;

public sealed class ConversationEndpointTests : IClassFixture<LocalAssistantApiFactory>
{
    private readonly HttpClient _client;
    private readonly LocalAssistantApiFactory _factory;

    public ConversationEndpointTests(LocalAssistantApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DirectScenarioReturnsFinalResponseWithoutTools()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello LocalAssistant", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var root = body.RootElement;
        Assert.NotEqual(Guid.Empty, root.GetProperty("conversationId").GetGuid());
        Assert.Equal("Fake response: Hello LocalAssistant", root.GetProperty("content").GetString());
        Assert.Equal(1, root.GetProperty("iterations").GetInt32());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task TimeScenarioExecutesToolAndReturnsDeterministicTime()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var root = body.RootElement;
        Assert.Equal(
            "Current UTC time is 2026-08-17T14:30:00.0000000+00:00.",
            root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("iterations").GetInt32());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("get_current_time", tool.GetProperty("toolName").GetString());
        Assert.True(tool.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task UnknownScenarioReturnsValidationProblem()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", scenario = "unknown" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownProviderReturnsValidationProblem()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", provider = "unknown" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OllamaWithoutConfiguredModelReturnsValidationProblem()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", provider = "ollama" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OllamaModelWithoutToolsReturnsValidationProblem()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Ollama:Model", "test-model");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<OllamaModelInspector>()
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new StaticHttpMessageHandler(
                            """{ "capabilities": ["completion"] }"""));
            });
        }).CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", provider = "ollama" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal(
            "The configured Ollama model 'test-model' does not support tools.",
            Assert.Single(errors.GetProperty("provider").EnumerateArray()).GetString());
    }
}

public sealed class LocalAssistantApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(
                new ManualTimeProvider(new DateTimeOffset(2026, 8, 17, 14, 30, 0, TimeSpan.Zero)));
        });
    }
}

file sealed class StaticHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseJson;

    public StaticHttpMessageHandler(string responseJson)
    {
        _responseJson = responseJson;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
