using System.Collections.Concurrent;

namespace LocalAssistant.Core.Orchestration;

public enum ToolAuditEventType
{
    Requested,
    ToolNotFound,
    PolicyDenied,
    ConfirmationRequested,
    ConfirmationApproved,
    ConfirmationRejected,
    ConfirmationExpired,
    ConfirmationAccessDenied,
    ExecutionStarted,
    ExecutionSucceeded,
    ExecutionFailed,
    ExecutionTimedOut,
}

public sealed record ToolAuditEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    ToolAuditEventType Type,
    Guid ConversationId,
    string? PrincipalId,
    string ProviderName,
    string ToolCallId,
    string ToolName,
    string? OutcomeCode = null,
    Guid? ConfirmationId = null,
    double? DurationMilliseconds = null);

public interface IToolAuditSink
{
    ValueTask WriteAsync(ToolAuditEvent auditEvent, CancellationToken cancellationToken);
}

public sealed class InMemoryToolAuditSink : IToolAuditSink
{
    private readonly ConcurrentQueue<ToolAuditEvent> _events = new();

    public IReadOnlyList<ToolAuditEvent> Events => _events.ToArray();

    public ValueTask WriteAsync(ToolAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }
}
