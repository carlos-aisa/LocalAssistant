using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Api;

public sealed class ConversationEndpointTests : IClassFixture<LocalAssistantApiFactory>
{
    private const string LocalApiKey = "local-assistant-test-key";
    private static readonly string LocalApiKeyHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(LocalApiKey)));
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
    public async Task ConfiguredIdentityKeepsPublicRequestsAvailableToAnonymousClients()
    {
        using var client = CreateIdentityClient(["time.read"]);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousClientCannotContinueAnAuthenticatedConversation()
    {
        using var client = CreateIdentityClient(["time.read"]);
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);

        using var ownerResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Owner message", scenario = "direct" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        using var ownerBody = await JsonDocument.ParseAsync(
            await ownerResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = ownerBody.RootElement.GetProperty("conversationId").GetGuid();

        using var continuedOwnerResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Owner follow-up", conversationId, scenario = "direct" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, continuedOwnerResponse.StatusCode);

        client.DefaultRequestHeaders.Remove(LocalApiKeyAuthenticationDefaults.HeaderName);
        using var anonymousResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Anonymous access", conversationId, scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, anonymousResponse.StatusCode);
        using var anonymousBody = await JsonDocument.ParseAsync(
            await anonymousResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        Assert.Equal(
            "conversation_not_found",
            anonymousBody.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnonymousClientCanContinueAnAnonymousConversation()
    {
        using var client = CreateIdentityClient(["time.read"]);

        using var firstResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Public message", scenario = "direct" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstBody = await JsonDocument.ParseAsync(
            await firstResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = firstBody.RootElement.GetProperty("conversationId").GetGuid();

        using var secondResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Public follow-up", conversationId, scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
    }

    [Fact]
    public async Task InvalidApiKeyIsRejectedBeforeTheOrchestrator()
    {
        using var client = CreateIdentityClient(["time.read"]);
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            "invalid-api-key");

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BootstrappedInstallationAuthenticatesItsOwner()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = new FileInstallationIdentityStore(
            Options.Create(new InstallationIdentityOptions { StateDirectory = stateDirectory.Path }),
            TimeProvider.System);
        var bootstrap = await store.BootstrapAsync(CancellationToken.None);
        Assert.Equal(InstallationBootstrapStatus.Created, bootstrap.Status);

        using var client = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("LocalAssistant:Installation:StateDirectory", stateDirectory.Path))
            .CreateClient();
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            bootstrap.ApiKey);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Owner message", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScopedToolRequiresAConfiguredAuthenticatedPrincipal()
    {
        var tool = new ScopedCurrentTimeTool();
        using var anonymousClient = CreateIdentityClient(["time.read"], tool);
        using var anonymousResponse = await anonymousClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        using var noScopeClient = CreateIdentityClient([], tool);
        noScopeClient.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);
        using var noScopeResponse = await noScopeClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        using var authorizedClient = CreateIdentityClient(["time.read"], tool);
        authorizedClient.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);
        using var authorizedResponse = await authorizedClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noScopeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorizedResponse.StatusCode);
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
    public async Task ToolFailureDoesNotExposeProviderContentToTheApiClient()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IToolRegistry>();
                services.AddSingleton<IToolRegistry>(_ => new ToolRegistry([new FailingCurrentTimeTool()]));
            });
        }).CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.DoesNotContain("Sensitive provider detail", responseBody, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(responseBody);
        Assert.Equal("The time service could not complete the request.", body.RootElement
            .GetProperty("error")
            .GetProperty("message")
            .GetString());
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

    private HttpClient CreateIdentityClient(
        string[] scopes,
        ITool? replacementTool = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Identity:Enabled", "true");
            builder.UseSetting("LocalAssistant:Identity:PrincipalId", "test-owner");
            builder.UseSetting("LocalAssistant:Identity:ApiKeySha256", LocalApiKeyHash);
            for (var index = 0; index < scopes.Length; index++)
            {
                builder.UseSetting($"LocalAssistant:Identity:Scopes:{index}", scopes[index]);
            }

            if (replacementTool is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IToolRegistry>();
                    services.AddSingleton<IToolRegistry>(_ => new ToolRegistry([replacementTool]));
                });
            }
        }).CreateClient();
    }
}

public sealed class LocalAssistantApiFactory : WebApplicationFactory<Program>
{
    private readonly string _installationStateDirectory = Path.Combine(
        Path.GetTempPath(),
        $"LocalAssistant.ApiTests.{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(
            "LocalAssistant:Installation:StateDirectory",
            _installationStateDirectory);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(
                new ManualTimeProvider(new DateTimeOffset(2026, 8, 17, 14, 30, 0, TimeSpan.Zero)));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_installationStateDirectory))
        {
            Directory.Delete(_installationStateDirectory, recursive: true);
        }
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
                new ToolRiskProfile(
                    ToolOperationImpact.ChangesState,
                    ToolDataSensitivity.Public,
                    ToolExposure.Local,
                    ToolCost.None,
                    RequiresConfirmation: true,
                    [])),
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

file sealed class ScopedCurrentTimeTool : ITool
{
    private readonly CurrentTimeTool _inner = new(TimeProvider.System);

    public ScopedCurrentTimeTool()
    {
        Definition = new ToolDefinition(
            new ToolMetadata(
                CurrentTimeTool.ToolName,
                "Reads the current time for an authorized principal.",
                new ToolRiskProfile(
                    ToolOperationImpact.ReadOnly,
                    ToolDataSensitivity.Private,
                    ToolExposure.Local,
                    ToolCost.None,
                    RequiresConfirmation: false,
                    ["time.read"])),
            _inner.Definition.InputSchema);
    }

    public ToolDefinition Definition { get; }

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) => _inner.ExecuteAsync(arguments, cancellationToken);
}

file sealed class FailingCurrentTimeTool : ITool
{
    private readonly CurrentTimeTool _inner = new(TimeProvider.System);

    public ToolDefinition Definition => _inner.Definition;

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionResult.Failure(
            "tool_execution_failed",
            "Sensitive provider detail",
            "The time service could not complete the request."));
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
