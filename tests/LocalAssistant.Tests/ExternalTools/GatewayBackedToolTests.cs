using System.Text.Json;
using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Security.Egress;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.ExternalTools;

public sealed class GatewayBackedToolTests
{
    [Fact]
    public void RejectsAnOperationThatIsNotControlledExternal()
    {
        var operation = new TestOperation(ToolExposure.Local);

        var exception = Assert.Throws<ArgumentException>(() =>
            new GatewayBackedTool(operation, new CountingGateway()));

        Assert.Equal("operation", exception.ParamName);
    }

    [Fact]
    public async Task RejectsInvalidArgumentsWithoutCallingTheGateway()
    {
        var operation = new TestOperation(
            ToolExposure.ControlledExternal,
            static (_, _) => ValueTask.FromResult<ExternalToolRequest?>(null));
        var gateway = new CountingGateway();
        var sut = new GatewayBackedTool(operation, gateway);

        var result = await sut.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { unexpected = true }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
        Assert.Equal(0, gateway.CallCount);
    }

    [Fact]
    public async Task ConvertsValidationExceptionsToInvalidArgumentsWithoutCallingTheGateway()
    {
        var operation = new TestOperation(
            ToolExposure.ControlledExternal,
            static (_, _) => throw new JsonException("The supplied value is invalid."));
        var gateway = new CountingGateway();
        var sut = new GatewayBackedTool(operation, gateway);

        var result = await sut.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { unexpected = true }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
        Assert.Equal(0, gateway.CallCount);
    }

    [Fact]
    public async Task ValidatesArgumentsBeforeDispatchingThroughTheGateway()
    {
        var request = CreateRequest();
        var operation = new TestOperation(
            ToolExposure.ControlledExternal,
            (arguments, _) => ValueTask.FromResult<ExternalToolRequest?>(
                arguments.TryGetProperty("query", out var query) &&
                query.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(query.GetString())
                    ? request
                    : null));
        var gateway = new CountingGateway(
            new ExternalToolGatewayResult(
                true,
                JsonSerializer.SerializeToElement(new { answer = "ok" }),
                null,
                null));
        var sut = new GatewayBackedTool(operation, gateway);

        var result = await sut.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { query = "weather" }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, gateway.CallCount);
        Assert.Same(request, gateway.LastRequest);
    }

    [Fact]
    public void AllowsControlledExternalGatewayRouteToReachRiskPolicy()
    {
        var operation = new TestOperation(ToolExposure.ControlledExternal);
        var tool = new GatewayBackedTool(operation, new CountingGateway());
        var policy = new DefaultToolRiskPolicy();

        var result = policy.Evaluate(
            ToolPolicyTarget.FromRegisteredTool(tool),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Allowed, result.Kind);
    }

    [Fact]
    public void AppliesScopesAfterAllowingTheControlledExternalGatewayRoute()
    {
        var operation = new TestOperation(
            ToolExposure.ControlledExternal,
            requiredScopes: ["external.read"]);
        var tool = new GatewayBackedTool(operation, new CountingGateway());
        var policy = new DefaultToolRiskPolicy();

        var result = policy.Evaluate(
            ToolPolicyTarget.FromRegisteredTool(tool),
            new ToolPolicyContext(
                "test-principal",
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("scope_not_granted", result.Code);
    }

    [Fact]
    public void RejectsGatewayRouteWhoseOperationChangesToLocalExposure()
    {
        var operation = new TestOperation(ToolExposure.ControlledExternal);
        var tool = new GatewayBackedTool(operation, new CountingGateway());
        operation.Exposure = ToolExposure.Local;
        var policy = new DefaultToolRiskPolicy();

        var result = policy.Evaluate(
            ToolPolicyTarget.FromRegisteredTool(tool),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("invalid_gateway_tool_configuration", result.Code);
    }

    private static ExternalToolRequest CreateRequest() => new(
        "test-adapter",
        "test-operation",
        "test-purpose",
        [new ExternalToolField(
            new EgressPayloadField("query", [DataCategory.PublicData], true, true),
            JsonSerializer.SerializeToElement("weather"))]);

    private sealed class TestOperation : IGatewayToolOperation
    {
        private readonly Func<JsonElement, CancellationToken, ValueTask<ExternalToolRequest?>> _createRequest;

        public TestOperation(
            ToolExposure exposure,
            Func<JsonElement, CancellationToken, ValueTask<ExternalToolRequest?>>? createRequest = null,
            IReadOnlyList<string>? requiredScopes = null)
        {
            Exposure = exposure;
            _createRequest = createRequest ?? ((_, _) => ValueTask.FromResult<ExternalToolRequest?>(CreateRequest()));
            RequiredScopes = requiredScopes ?? [];
        }

        public ToolExposure Exposure { get; set; }

        public IReadOnlyList<string> RequiredScopes { get; }

        public ToolDefinition Definition => new(
            new ToolMetadata(
                "test-gateway-tool",
                "Test gateway tool",
                new ToolRiskProfile(
                    ToolOperationImpact.ReadOnly,
                    ToolDataSensitivity.Public,
                    Exposure,
                    ToolCost.None,
                    RequiresConfirmation: false,
                    RequiredScopes)),
            JsonSerializer.SerializeToElement(new { type = "object" }));

        public ValueTask<ExternalToolRequest?> CreateRequestAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) => _createRequest(arguments, cancellationToken);

        public ToolExecutionResult CreateResult(ExternalToolGatewayResult gatewayResult) =>
            gatewayResult.IsSuccess
                ? ToolExecutionResult.Success("ok")
                : ToolExecutionResult.Failure(
                    gatewayResult.ErrorCode ?? "external_adapter_failed",
                    "The external tool operation failed.");
    }

    private sealed class CountingGateway(ExternalToolGatewayResult? result = null) : IExternalToolsGateway
    {
        private readonly ExternalToolGatewayResult _result = result ??
            new ExternalToolGatewayResult(
                true,
                JsonSerializer.SerializeToElement(new { ok = true }),
                null,
                null);

        public int CallCount { get; private set; }

        public ExternalToolRequest? LastRequest { get; private set; }

        public ValueTask<ExternalToolGatewayResult> ExecuteAsync(
            ExternalToolRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(_result);
        }
    }
}
