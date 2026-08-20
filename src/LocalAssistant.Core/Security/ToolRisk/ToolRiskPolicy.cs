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

public interface IToolRiskPolicy
{
    ToolPolicyDecision Evaluate(ToolMetadata metadata, ToolPolicyContext context);
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
    public ToolPolicyDecision Evaluate(ToolMetadata metadata, ToolPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);

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

        if (metadata.Risk.Exposure == ToolExposure.ControlledExternal)
        {
            return new ToolPolicyDecision(ToolPolicyDecisionKind.Denied, "external_gateway_required");
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
