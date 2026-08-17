namespace LocalAssistant.Core.Orchestration;

public sealed record ConversationTurnRequest(
    string Message,
    Guid? ConversationId = null,
    IReadOnlySet<string>? ApprovedTools = null);

public sealed record OrchestrationError(string Code, string Message, string? ToolName = null);

public sealed record ToolExecutionTrace(
    string ToolCallId,
    string ToolName,
    bool Succeeded,
    double DurationMilliseconds,
    string? ErrorCode = null);

public sealed record ExecutionTimings(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double TotalMilliseconds,
    double ProviderMilliseconds,
    double ToolsMilliseconds);

public sealed record ConversationTurnResult(
    Guid ConversationId,
    string? Content,
    IReadOnlyList<ToolExecutionTrace> Tools,
    int Iterations,
    ExecutionTimings Timings,
    OrchestrationError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed class OrchestrationOptions
{
    public int MaxIterations { get; init; } = 5;

    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public interface IConversationOrchestrator
{
    Task<ConversationTurnResult> ProcessAsync(
        ConversationTurnRequest request,
        LanguageModels.ILanguageProvider provider,
        CancellationToken cancellationToken);
}
