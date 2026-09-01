using System.Text.Json;

namespace LocalAssistant.TerminalClient;

public sealed record CreatePrivateSessionRequest(string ClientId, string Credential);

public sealed record HealthResponse;

public sealed record PrivateSessionResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);

public sealed record CompletePrivateClientPairingRequest(string Challenge, string DisplayName);

public sealed record PrivateClientCredentialResponse(string ClientId, string DisplayName, string Credential);

public sealed record ConsumeAdministrativeChallengeRequest(string Challenge, string ClientId);

public sealed record PrivateClientRevocationResponse(string ClientId);

public sealed record CompletionResponse;

public sealed record SendMessageRequest(
    string Message,
    Guid? ConversationId,
    string Provider,
    string Scenario);

public sealed record ConversationResponse(
    Guid ConversationId,
    string? Content,
    IReadOnlyList<ToolExecutionTraceResponse> Tools,
    int Iterations,
    ConversationErrorResponse? Error,
    ToolConfirmationResponse? Confirmation);

public sealed record ToolExecutionTraceResponse(
    string ToolCallId,
    string ToolName,
    bool Succeeded,
    double DurationMilliseconds,
    string? ErrorCode);

public sealed record ConversationErrorResponse(string Code, string Message, string? ToolName);

public sealed record ToolConfirmationResponse(
    Guid ConfirmationId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    DateTimeOffset ExpiresAtUtc);

public sealed record ResolveToolConfirmationRequest(bool Approved, string Provider, string Scenario);

public sealed record ClientError(
    string Code,
    string Message,
    bool IsUncertain = false,
    bool CanRenewSession = false);

public sealed record ClientResult<T>(T? Value, ClientError? Error)
{
    public bool IsSuccess => Error is null && Value is not null;
}

public static class ClientResults
{
    public static ClientResult<T> Success<T>(T value) => new(value, null);

    public static ClientResult<T> Failure<T>(
        string code,
        string message,
        bool isUncertain = false,
        bool canRenewSession = false) =>
        new(default, new ClientError(code, message, isUncertain, canRenewSession));
}
