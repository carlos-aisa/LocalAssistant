using System.Text.Json;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.Security.ToolRisk;

public sealed class DefaultToolRiskPolicyTests
{
    private readonly DefaultToolRiskPolicy _sut = new();

    [Fact]
    public void AllowsPublicLocalRead()
    {
        var result = _sut.Evaluate(Target(ToolRiskProfile.PublicLocalRead), ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Allowed, result.Kind);
    }

    [Fact]
    public void DeniesSensitiveToolForAnonymousContext()
    {
        var result = _sut.Evaluate(
            Target(Profile(sensitivity: ToolDataSensitivity.Sensitive)),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("authentication_required", result.Code);
    }

    [Fact]
    public void DeniesMissingScope()
    {
        var result = _sut.Evaluate(
            Target(Profile(requiredScopes: ["documents.read"])),
            new ToolPolicyContext("test-principal", new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("scope_not_granted", result.Code);
    }

    [Fact]
    public void RequiresAuthenticationForScopedTool()
    {
        var result = _sut.Evaluate(
            Target(Profile(requiredScopes: ["documents.read"])),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("authentication_required", result.Code);
    }

    [Fact]
    public void RequiresConfirmationForSignificantCost()
    {
        var result = _sut.Evaluate(
            Target(Profile(cost: ToolCost.Significant)),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.RequiresConfirmation, result.Kind);
    }

    [Fact]
    public void DeniesControlledExternalToolUntilItUsesGateway()
    {
        var result = _sut.Evaluate(
            Target(Profile(exposure: ToolExposure.ControlledExternal)),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("external_gateway_required", result.Code);
    }

    private static ToolPolicyTarget Target(ToolRiskProfile risk) =>
        ToolPolicyTarget.FromRegisteredTool(new TestTool(risk));

    private static ToolRiskProfile Profile(
        ToolDataSensitivity sensitivity = ToolDataSensitivity.Public,
        ToolExposure exposure = ToolExposure.Local,
        ToolCost cost = ToolCost.None,
        IReadOnlyList<string>? requiredScopes = null) =>
        new(
            ToolOperationImpact.ReadOnly,
            sensitivity,
            exposure,
            cost,
            RequiresConfirmation: false,
            requiredScopes ?? []);

    private sealed class TestTool(ToolRiskProfile risk) : ITool
    {
        public ToolDefinition Definition { get; } = new(
            new ToolMetadata("test", "Test tool", risk),
            JsonSerializer.SerializeToElement(new { type = "object" }));

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolExecutionResult.Success("ok"));
    }
}
