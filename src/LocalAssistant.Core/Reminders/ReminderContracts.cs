using System.Collections.Concurrent;

namespace LocalAssistant.Core.Reminders;

public sealed record Reminder(
    Guid Id,
    string PrincipalId,
    string Title,
    DateTimeOffset DueAtUtc,
    DateTimeOffset CreatedAtUtc);

public interface IReminderStore
{
    ValueTask<Reminder> GetOrCreateAsync(
        string principalId,
        Guid operationId,
        string title,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken);
}

public sealed class InMemoryReminderStore : IReminderStore
{
    private readonly ConcurrentDictionary<ReminderOperationKey, Reminder> _reminders = new();
    private readonly TimeProvider _clock;

    public InMemoryReminderStore(TimeProvider clock)
    {
        _clock = clock;
    }

    public ValueTask<Reminder> GetOrCreateAsync(
        string principalId,
        Guid operationId,
        string title,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = new ReminderOperationKey(principalId, operationId);
        var reminder = _reminders.GetOrAdd(
            key,
            _ => new Reminder(
                Guid.NewGuid(),
                principalId,
                title,
                dueAtUtc,
                _clock.GetUtcNow()));

        return ValueTask.FromResult(reminder);
    }

    private sealed record ReminderOperationKey(string PrincipalId, Guid OperationId);
}
