using LocalAssistant.Core.Orchestration;

namespace LocalAssistant.Api.Contracts;

public sealed record SendMessageRequest(
    string Message,
    Guid? ConversationId = null,
    string Provider = "fake",
    string Scenario = "direct",
    IReadOnlyList<string>? ApprovedTools = null);

public sealed record ConversationApiResponse(
    Guid ConversationId,
    string? Content,
    IReadOnlyList<ToolExecutionTrace> Tools,
    int Iterations,
    ExecutionTimings Timings,
    OrchestrationError? Error)
{
    public static ConversationApiResponse FromResult(ConversationTurnResult result) => new(
        result.ConversationId,
        result.Content,
        result.Tools,
        result.Iterations,
        result.Timings,
        result.Error);
}
