using Microsoft.Extensions.Logging;

namespace LocalAssistant.Core.Orchestration;

internal static partial class OrchestrationLog
{
    [LoggerMessage(1000, LogLevel.Information, "Conversation turn started for {ConversationId}")]
    public static partial void TurnStarted(ILogger logger, Guid conversationId);

    [LoggerMessage(1001, LogLevel.Information, "Calling provider {ProviderName} at iteration {Iteration} for {ConversationId}")]
    public static partial void ProviderCalled(
        ILogger logger,
        string providerName,
        int iteration,
        Guid conversationId);

    [LoggerMessage(1002, LogLevel.Information, "Provider requested tool {ToolName} with call {ToolCallId} for {ConversationId}")]
    public static partial void ToolRequested(
        ILogger logger,
        string toolName,
        string toolCallId,
        Guid conversationId);

    [LoggerMessage(1003, LogLevel.Information, "Tool {ToolName} call {ToolCallId} completed in {DurationMilliseconds} ms for {ConversationId}")]
    public static partial void ToolCompleted(
        ILogger logger,
        string toolName,
        string toolCallId,
        double durationMilliseconds,
        Guid conversationId);

    [LoggerMessage(1004, LogLevel.Warning, "Tool {ToolName} call {ToolCallId} failed with {ErrorCode} for {ConversationId}")]
    public static partial void ToolFailed(
        ILogger logger,
        string toolName,
        string toolCallId,
        string errorCode,
        Guid conversationId,
        Exception? exception = null);

    [LoggerMessage(1005, LogLevel.Information, "Conversation turn finished for {ConversationId} after {Iterations} iterations in {DurationMilliseconds} ms")]
    public static partial void TurnCompleted(
        ILogger logger,
        Guid conversationId,
        int iterations,
        double durationMilliseconds);

    [LoggerMessage(1006, LogLevel.Warning, "Provider {ProviderName} timed out at iteration {Iteration} for {ConversationId}")]
    public static partial void ProviderTimedOut(
        ILogger logger,
        string providerName,
        int iteration,
        Guid conversationId);

    [LoggerMessage(1007, LogLevel.Error, "Provider {ProviderName} failed at iteration {Iteration} for {ConversationId}")]
    public static partial void ProviderFailed(
        ILogger logger,
        string providerName,
        int iteration,
        Guid conversationId,
        Exception exception);
}
