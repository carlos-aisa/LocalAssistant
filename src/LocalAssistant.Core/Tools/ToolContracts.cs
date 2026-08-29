using System.Text.Json;

namespace LocalAssistant.Core.Tools;

public enum ToolOperationImpact
{
    ReadOnly,
    ChangesState,
    Executes,
}

public enum ToolDataSensitivity { Public, Private, Sensitive }

public enum ToolExposure { Local, ControlledExternal }

public enum ToolCost { None, Bounded, Significant }

public sealed record ToolRiskProfile(
    ToolOperationImpact Impact,
    ToolDataSensitivity Sensitivity,
    ToolExposure Exposure,
    ToolCost Cost,
    bool RequiresConfirmation,
    IReadOnlyList<string> RequiredScopes)
{
    public static ToolRiskProfile PublicLocalRead { get; } = new(
        ToolOperationImpact.ReadOnly,
        ToolDataSensitivity.Public,
        ToolExposure.Local,
        ToolCost.None,
        RequiresConfirmation: false,
        []);
}

public sealed record ToolMetadata(
    string Name,
    string Description,
    ToolRiskProfile Risk);

public sealed record ToolDefinition(ToolMetadata Metadata, JsonElement InputSchema);

public sealed record ToolExecutionResult(
    bool IsSuccess,
    string Content,
    string? ErrorCode = null,
    string? ClientMessage = null)
{
    public static ToolExecutionResult Success(string content) => new(true, content);

    public static ToolExecutionResult Failure(
        string errorCode,
        string providerContent,
        string? clientMessage = null) =>
        new(false, providerContent, errorCode, clientMessage);
}

public sealed record ToolExecutionContext(
    Guid ConversationId,
    string? PrincipalId,
    Guid? OperationId);

public interface ITool
{
    ToolDefinition Definition { get; }

    ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken);

    ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken) => ExecuteAsync(arguments, cancellationToken);
}

public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    bool TryGet(string name, out ITool? tool);
}
