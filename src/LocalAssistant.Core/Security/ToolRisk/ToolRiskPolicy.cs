using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Core.Security.ToolRisk;

public sealed record ToolPolicyContext(
    string? PrincipalId,
    IReadOnlySet<string> GrantedScopes)
{
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(PrincipalId);

    public static ToolPolicyContext Anonymous => new(
        null,
        new HashSet<string>(StringComparer.Ordinal));
}

public enum ToolPolicyDecisionKind { Allowed, RequiresConfirmation, Denied }

public sealed record ToolPolicyDecision(ToolPolicyDecisionKind Kind, string? Code = null);

public enum ToolExecutionRoute
{
    Standard,
    GatewayBacked,
}

public sealed record ToolPolicyTarget
{
    private ToolPolicyTarget(ToolMetadata metadata, ToolExecutionRoute route)
    {
        Metadata = metadata;
        Route = route;
    }

    public ToolMetadata Metadata { get; }

    public ToolExecutionRoute Route { get; }

    public static ToolPolicyTarget FromRegisteredTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return new ToolPolicyTarget(
            tool.Definition.Metadata,
            tool is GatewayBackedTool
                ? ToolExecutionRoute.GatewayBacked
                : ToolExecutionRoute.Standard);
    }
}

public interface IToolRiskPolicy
{
    ToolPolicyDecision Evaluate(ToolPolicyTarget target, ToolPolicyContext context);
}

public interface IToolPolicyContextAccessor
{
    ToolPolicyContext GetCurrent();
}

public sealed class AnonymousToolPolicyContextAccessor : IToolPolicyContextAccessor
{
    public ToolPolicyContext GetCurrent() => ToolPolicyContext.Anonymous;
}

public sealed class DefaultToolRiskPolicy : IToolRiskPolicy
{
    public ToolPolicyDecision Evaluate(ToolPolicyTarget target, ToolPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var metadata = target.Metadata;

        if (metadata.Risk.Exposure == ToolExposure.Local &&
            target.Route == ToolExecutionRoute.GatewayBacked)
        {
            return new ToolPolicyDecision(
                ToolPolicyDecisionKind.Denied,
                "invalid_gateway_tool_configuration");
        }

        if (metadata.Risk.Exposure == ToolExposure.ControlledExternal &&
            target.Route == ToolExecutionRoute.Standard)
        {
            return new ToolPolicyDecision(
                ToolPolicyDecisionKind.Denied,
                "external_gateway_required");
        }

        if ((metadata.Risk.Sensitivity is ToolDataSensitivity.Private or ToolDataSensitivity.Sensitive) &&
            !context.IsAuthenticated)
        {
            return new ToolPolicyDecision(ToolPolicyDecisionKind.Denied, "authentication_required");
        }

        if (metadata.Risk.RequiredScopes.Count > 0 && !context.IsAuthenticated)
        {
            return new ToolPolicyDecision(ToolPolicyDecisionKind.Denied, "authentication_required");
        }

        if (metadata.Risk.RequiredScopes.Any(scope => !context.GrantedScopes.Contains(scope)))
        {
            return new ToolPolicyDecision(ToolPolicyDecisionKind.Denied, "scope_not_granted");
        }

        if (metadata.Risk.RequiresConfirmation ||
            metadata.Risk.Impact is ToolOperationImpact.ChangesState or ToolOperationImpact.Executes ||
            metadata.Risk.Cost == ToolCost.Significant)
        {
            return new ToolPolicyDecision(ToolPolicyDecisionKind.RequiresConfirmation);
        }

        return new ToolPolicyDecision(ToolPolicyDecisionKind.Allowed);
    }
}
