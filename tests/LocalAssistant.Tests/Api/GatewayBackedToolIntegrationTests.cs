using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Security.Egress;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.ExternalTools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAssistant.Tests.Api;

public sealed class GatewayBackedToolIntegrationTests
{
    [Fact]
    public void ProductionCompositionRegistersAnEmptyExternalGatewayWithoutGatewayTools()
    {
        using var factory = new LocalAssistantApiFactory();

        var gateway = factory.Services.GetRequiredService<IExternalToolsGateway>();
        var adapters = factory.Services.GetServices<IExternalToolAdapter>();
        var tools = factory.Services.GetRequiredService<IToolRegistry>();

        Assert.IsType<ControlledExternalToolsGateway>(gateway);
        Assert.Empty(adapters);
        Assert.DoesNotContain(tools.Tools, tool => tool is GatewayBackedTool);
    }

    [Fact]
    public async Task GatewayBackedTestToolExecutesThroughTheConversationEndpoint()
    {
        using var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITool>();
                services.RemoveAll<IToolRegistry>();
                services.RemoveAll<IExternalToolAdapter>();
                services.RemoveAll<IExternalToolsGateway>();

                services.AddSingleton<IExternalToolAdapter, GatewayTestAdapter>();
                services.AddSingleton<IExternalToolsGateway>(serviceProvider =>
                    new ControlledExternalToolsGateway(
                        serviceProvider.GetServices<IExternalToolAdapter>(),
                        serviceProvider.GetRequiredService<IEgressPolicy>(),
                        NullLogger<ControlledExternalToolsGateway>.Instance,
                        Microsoft.Extensions.Options.Options.Create(
                            new ExternalToolsGatewayOptions())));
                services.AddSingleton<ITool>(serviceProvider => new GatewayBackedTool(
                    new GatewayTimeOperation(),
                    serviceProvider.GetRequiredService<IExternalToolsGateway>()));
                services.AddSingleton<IToolRegistry>(serviceProvider =>
                    new ToolRegistry(serviceProvider.GetServices<ITool>()));
            });
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new
            {
                message = "Run the time scenario.",
                provider = "fake",
                scenario = "time",
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.True(tool.GetProperty("succeeded").GetBoolean());
        Assert.Equal(CurrentTimeTool.ToolName, tool.GetProperty("toolName").GetString());
        Assert.Equal(
            "Current UTC time is 2026-08-17T14:30:00Z.",
            body.RootElement.GetProperty("content").GetString());
    }

    private sealed class GatewayTimeOperation : IGatewayToolOperation
    {
        private static readonly ToolDefinition ToolDefinition = new(
            new ToolMetadata(
                CurrentTimeTool.ToolName,
                "Gets the current UTC time through the gateway test adapter.",
                new ToolRiskProfile(
                    ToolOperationImpact.ReadOnly,
                    ToolDataSensitivity.Public,
                    ToolExposure.ControlledExternal,
                    ToolCost.None,
                    RequiresConfirmation: false,
                    [])),
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = false,
            }));

        public ToolDefinition Definition => ToolDefinition;

        public ValueTask<ExternalToolRequest?> CreateRequestAsync(
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            {
                return ValueTask.FromResult<ExternalToolRequest?>(null);
            }

            return ValueTask.FromResult<ExternalToolRequest?>(new ExternalToolRequest(
                "gateway-time-test",
                "get-utc-time",
                "integration-test",
                [new ExternalToolField(
                    new EgressPayloadField("source", [DataCategory.PublicData], true, true),
                    JsonSerializer.SerializeToElement("test"))]));
        }

        public ToolExecutionResult CreateResult(ExternalToolGatewayResult gatewayResult)
        {
            if (!gatewayResult.IsSuccess || gatewayResult.Content is null)
            {
                return ToolExecutionResult.Failure(
                    gatewayResult.ErrorCode ?? "external_adapter_failed",
                    "The external tool operation failed.");
            }

            return ToolExecutionResult.Success(gatewayResult.Content.Value.GetRawText());
        }
    }

    private sealed class GatewayTestAdapter : IExternalToolAdapter
    {
        public string Name => "gateway-time-test";

        public string Destination => "test://gateway-time";

        public IReadOnlySet<string> SupportedOperations { get; } =
            new HashSet<string>(["get-utc-time"], StringComparer.Ordinal);

        public ValueTask<ExternalToolAdapterResult> ExecuteAsync(
            ExternalToolPayload payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExternalToolAdapterResult.Success(
                JsonSerializer.SerializeToElement(new { utc = "2026-08-17T14:30:00Z" })));
    }
}
