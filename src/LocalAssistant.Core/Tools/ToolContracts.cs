using System.Text.Json;

namespace LocalAssistant.Core.Tools;

public enum ToolImpact
{
    ReadOnly,
    ChangesState,
}

public sealed record ToolMetadata(
    string Name,
    string Description,
    ToolImpact Impact,
    bool RequiresConfirmation);

public sealed record ToolDefinition(ToolMetadata Metadata, JsonElement InputSchema);

public sealed record ToolExecutionResult(bool IsSuccess, string Content, string? ErrorCode = null)
{
    public static ToolExecutionResult Success(string content) => new(true, content);

    public static ToolExecutionResult Failure(string errorCode, string message) =>
        new(false, message, errorCode);
}

public interface ITool
{
    ToolDefinition Definition { get; }

    ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken);
}

public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    bool TryGet(string name, out ITool? tool);
}
