using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task TemperatureScenarioExecutesToolAndReturnsDeterministicConversion()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Convert 100 Celsius to Fahrenheit", scenario = "temperature" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var root = body.RootElement;
        Assert.Equal("100 Celsius is 212 fahrenheit.", root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("iterations").GetInt32());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("convert_temperature", tool.GetProperty("toolName").GetString());
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
    public async Task UnknownToolConfirmationReturnsNotFound()
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/tool-confirmations/{Guid.NewGuid()}/decisions",
            new { approved = true },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmationEndpointExecutesOnlyThePendingToolCall()
    {
        var tool = new ConfirmationTemperatureTool();
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IToolRegistry>();
                services.AddSingleton<IToolRegistry>(_ => new ToolRegistry([tool]));
            });
        }).CreateClient();

        using var pendingResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Convert 100 Celsius to Fahrenheit", scenario = "temperature" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, pendingResponse.StatusCode);
        using var pendingBody = await JsonDocument.ParseAsync(
            await pendingResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = pendingBody.RootElement.GetProperty("conversationId").GetGuid();
        var confirmation = pendingBody.RootElement.GetProperty("confirmation");
        var confirmationId = confirmation.GetProperty("confirmationId").GetGuid();
        Assert.Equal("convert_temperature", confirmation.GetProperty("toolName").GetString());
        Assert.Equal(100, confirmation.GetProperty("arguments").GetProperty("value").GetInt32());
        Assert.Equal(0, tool.ExecutionCount);

        using var decisionResponse = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/tool-confirmations/{confirmationId}/decisions",
            new { approved = true, scenario = "temperature" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
        using var decisionBody = await JsonDocument.ParseAsync(
            await decisionResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        Assert.Equal("100 Celsius is 212 fahrenheit.", decisionBody.RootElement.GetProperty("content").GetString());
        Assert.Equal(1, tool.ExecutionCount);
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

file sealed class ConfirmationTemperatureTool : ITool
{
    private readonly TemperatureConversionTool _inner = new();

    public ConfirmationTemperatureTool()
    {
        Definition = new ToolDefinition(
            new ToolMetadata(
                TemperatureConversionTool.ToolName,
                "Converts one temperature between supported units.",
                ToolImpact.ChangesState,
                RequiresConfirmation: true),
            _inner.Definition.InputSchema);
    }

    public int ExecutionCount { get; private set; }

    public ToolDefinition Definition { get; }

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return await _inner.ExecuteAsync(arguments, cancellationToken);
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
