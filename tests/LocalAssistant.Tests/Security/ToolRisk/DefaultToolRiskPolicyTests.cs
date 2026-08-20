using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.Security.ToolRisk;

public sealed class DefaultToolRiskPolicyTests
{
    private readonly DefaultToolRiskPolicy _sut = new();

    [Fact]
    public void AllowsPublicLocalRead()
    {
        var result = _sut.Evaluate(Metadata(ToolRiskProfile.PublicLocalRead), ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Allowed, result.Kind);
    }

    [Fact]
    public void DeniesSensitiveToolForAnonymousContext()
    {
        var result = _sut.Evaluate(
            Metadata(Profile(sensitivity: ToolDataSensitivity.Sensitive)),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("authentication_required", result.Code);
    }

    [Fact]
    public void DeniesMissingScope()
    {
        var result = _sut.Evaluate(
            Metadata(Profile(requiredScopes: ["documents.read"])),
            new ToolPolicyContext(true, new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("scope_not_granted", result.Code);
    }

    [Fact]
    public void RequiresConfirmationForSignificantCost()
    {
        var result = _sut.Evaluate(
            Metadata(Profile(cost: ToolCost.Significant)),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.RequiresConfirmation, result.Kind);
    }

    [Fact]
    public void DeniesControlledExternalToolUntilItUsesGateway()
    {
        var result = _sut.Evaluate(
            Metadata(Profile(exposure: ToolExposure.ControlledExternal)),
            ToolPolicyContext.Anonymous);

        Assert.Equal(ToolPolicyDecisionKind.Denied, result.Kind);
        Assert.Equal("external_gateway_required", result.Code);
    }

    private static ToolMetadata Metadata(ToolRiskProfile risk) => new("test", "Test tool", risk);

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
}
