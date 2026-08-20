using LocalAssistant.Core.Orchestration;

namespace LocalAssistant.Api.Contracts;

public sealed record SendMessageRequest(
    string Message,
    Guid? ConversationId = null,
    string Provider = "fake",
    string Scenario = "direct");

public sealed record ResolveToolConfirmationRequest(
    bool Approved,
    string Provider = "fake",
    string Scenario = "direct");

public sealed record ConversationApiResponse(
    Guid ConversationId,
    string? Content,
    IReadOnlyList<ToolExecutionTrace> Tools,
    int Iterations,
    ExecutionTimings Timings,
    OrchestrationError? Error,
    ToolConfirmationRequest? Confirmation)
{
    public static ConversationApiResponse FromResult(ConversationTurnResult result) => new(
        result.ConversationId,
        result.Content,
        result.Tools,
        result.Iterations,
        result.Timings,
        result.Error,
        result.Confirmation);
}
