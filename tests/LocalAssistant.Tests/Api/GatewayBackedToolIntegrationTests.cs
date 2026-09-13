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
using Microsoft.Extensions.Options;

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
    public void ProductionCompositionRejectsAGatewayTimeoutThatCanRaceTheToolTimeout()
    {
        using var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("LocalAssistant:ExternalToolsGateway:TotalTimeout", "00:00:10"));

        var exception = Assert.Throws<OptionsValidationException>(() => _ = factory.Services);

        Assert.Contains("must be less than the orchestration tool timeout", exception.Message, StringComparison.Ordinal);
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
                            new ExternalToolsGatewayOptions()),
                        serviceProvider.GetRequiredService<IExternalToolsGatewayDeadlineFactory>()));
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

    [Fact]
    public async Task GatewayDeadlineWinsBeforeTheOuterToolTimeoutForABlockedAdapter()
    {
        using var deadlineFactory = new ManualDeadlineFactory();
        var adapter = new BlockingGatewayTestAdapter();
        using var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITool>();
                services.RemoveAll<IToolRegistry>();
                services.RemoveAll<IExternalToolAdapter>();
                services.RemoveAll<IExternalToolsGateway>();
                services.RemoveAll<IExternalToolsGatewayDeadlineFactory>();

                services.AddSingleton<IExternalToolAdapter>(adapter);
                services.AddSingleton<IExternalToolsGatewayDeadlineFactory>(deadlineFactory);
                services.AddSingleton<IExternalToolsGateway>(serviceProvider =>
                    new ControlledExternalToolsGateway(
                        serviceProvider.GetServices<IExternalToolAdapter>(),
                        serviceProvider.GetRequiredService<IEgressPolicy>(),
                        NullLogger<ControlledExternalToolsGateway>.Instance,
                        Microsoft.Extensions.Options.Options.Create(
                            new ExternalToolsGatewayOptions { TotalTimeout = TimeSpan.FromSeconds(8) }),
                        serviceProvider.GetRequiredService<IExternalToolsGatewayDeadlineFactory>()));
                services.AddSingleton<ITool>(serviceProvider => new GatewayBackedTool(
                    new GatewayTimeOperation(adapter.Name),
                    serviceProvider.GetRequiredService<IExternalToolsGateway>()));
                services.AddSingleton<IToolRegistry>(serviceProvider =>
                    new ToolRegistry(serviceProvider.GetServices<ITool>()));
            });
        });
        using var client = factory.CreateClient();

        var request = client.PostAsJsonAsync(
            "/api/conversations/messages",
            new
            {
                message = "Run the time scenario.",
                provider = "fake",
                scenario = "time",
            },
            CancellationToken.None);
        await adapter.Started.Task;
        deadlineFactory.CancelCurrentDeadline();

        try
        {
            using var response = await request;
            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(CancellationToken.None),
                cancellationToken: CancellationToken.None);
            var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());

            Assert.Equal("external_gateway_timeout", tool.GetProperty("errorCode").GetString());
            Assert.NotEqual("tool_timeout", tool.GetProperty("errorCode").GetString());
        }
        finally
        {
            adapter.Release();
        }
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

        private readonly string _adapterName;

        public GatewayTimeOperation(string adapterName = "gateway-time-test")
        {
            _adapterName = adapterName;
        }

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
                _adapterName,
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

    private sealed class BlockingGatewayTestAdapter : IExternalToolAdapter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "gateway-time-blocked";

        public string Destination => "test://gateway-time";

        public IReadOnlySet<string> SupportedOperations { get; } =
            new HashSet<string>(["get-utc-time"], StringComparer.Ordinal);

        public async ValueTask<ExternalToolAdapterResult> ExecuteAsync(
            ExternalToolPayload payload,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return ExternalToolAdapterResult.Success(JsonSerializer.SerializeToElement(new { utc = "unused" }));
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ManualDeadlineFactory : IExternalToolsGatewayDeadlineFactory, IDisposable
    {
        private CancellationTokenSource? _deadline;

        public CancellationTokenSource Create(TimeSpan timeout)
        {
            _deadline = new CancellationTokenSource();
            return _deadline;
        }

        public void CancelCurrentDeadline()
        {
            Assert.NotNull(_deadline);
            _deadline!.Cancel();
        }

        public void Dispose()
        {
            _deadline?.Dispose();
        }
    }
}
