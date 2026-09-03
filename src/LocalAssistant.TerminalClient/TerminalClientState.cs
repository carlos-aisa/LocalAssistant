namespace LocalAssistant.TerminalClient;

internal enum TerminalClientLifecycle
{
    Disconnected,
    Connecting,
    Authenticating,
    Ready,
    Closing,
    Closed,
    Blocked,
}

internal enum TerminalClientActivity
{
    None,
    ResumingConversation,
    SelectingConversation,
    SendingTurn,
    AwaitingConfirmation,
    ResolvingConfirmation,
    CompletingConversation,
    PlayingVoice,
}

internal enum TerminalClientErrorCategory
{
    Recoverable,
    Uncertain,
    Blocking,
}

internal sealed record TerminalClientOperationError(
    TerminalClientErrorCategory Category,
    string Code,
    string SafeMessage,
    string Operation);

internal sealed record TerminalClientPendingConfirmation(
    string ToolName,
    DateTimeOffset ExpiresAtUtc);

internal sealed record TerminalClientStateSnapshot(
    TerminalClientLifecycle Lifecycle,
    TerminalClientActivity Activity,
    TerminalClientOperationError? Error,
    string? Provider,
    Guid? ConversationId,
    TerminalClientPendingConfirmation? PendingConfirmation)
{
    public static TerminalClientStateSnapshot Initial { get; } = new(
        TerminalClientLifecycle.Disconnected,
        TerminalClientActivity.None,
        null,
        null,
        null,
        null);
}

internal interface ITerminalClientStateSink
{
    void OnStateChanged(TerminalClientStateSnapshot snapshot);
}

internal sealed class NullTerminalClientStateSink : ITerminalClientStateSink
{
    public static NullTerminalClientStateSink Instance { get; } = new();

    public void OnStateChanged(TerminalClientStateSnapshot snapshot)
    {
    }
}

internal sealed class TerminalClientStateCoordinator
{
    private readonly ITerminalClientStateSink _sink;
    private TerminalClientStateSnapshot _current = TerminalClientStateSnapshot.Initial;
    private bool _initialPublished;

    public TerminalClientStateCoordinator(ITerminalClientStateSink? sink)
    {
        _sink = sink ?? NullTerminalClientStateSink.Instance;
    }

    public TerminalClientStateSnapshot Current => _current;

    public bool PublishInitial()
    {
        if (_initialPublished)
        {
            return false;
        }

        _initialPublished = true;
        Notify(_current);
        return true;
    }

    public bool TryTransition(TerminalClientStateSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!IsSnapshotValid(next) || !IsTransitionAllowed(_current, next))
        {
            return false;
        }

        if (_current == next)
        {
            return true;
        }

        _current = next;
        Notify(next);
        return true;
    }

    private static bool IsSnapshotValid(TerminalClientStateSnapshot snapshot)
    {
        if (snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
            string.IsNullOrWhiteSpace(snapshot.Provider))
        {
            return false;
        }

        if (snapshot.Activity == TerminalClientActivity.AwaitingConfirmation)
        {
            if (snapshot.PendingConfirmation is null)
            {
                return false;
            }
        }
        else if (snapshot.PendingConfirmation is not null)
        {
            return false;
        }

        if (snapshot.Lifecycle == TerminalClientLifecycle.Blocked)
        {
            if (snapshot.Error?.Category != TerminalClientErrorCategory.Blocking)
            {
                return false;
            }
        }
        else if (snapshot.Lifecycle is not TerminalClientLifecycle.Closing and
                 not TerminalClientLifecycle.Closed &&
                 snapshot.Error?.Category == TerminalClientErrorCategory.Blocking)
        {
            return false;
        }

        if (snapshot.Lifecycle is TerminalClientLifecycle.Connecting or
            TerminalClientLifecycle.Authenticating)
        {
            return snapshot.ConversationId is null && snapshot.PendingConfirmation is null;
        }

        return true;
    }

    private static bool IsTransitionAllowed(
        TerminalClientStateSnapshot current,
        TerminalClientStateSnapshot next)
    {
        if (next.Activity == TerminalClientActivity.PlayingVoice)
        {
            return false;
        }

        if (current.Lifecycle == TerminalClientLifecycle.Closed)
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        if (next.Lifecycle == TerminalClientLifecycle.Closing)
        {
            return next.Activity == TerminalClientActivity.None;
        }

        if (current.Lifecycle == TerminalClientLifecycle.Closing)
        {
            return next.Lifecycle == TerminalClientLifecycle.Closed &&
                next.Activity == TerminalClientActivity.None;
        }

        if (current.Lifecycle == TerminalClientLifecycle.Blocked)
        {
            return next.Lifecycle == TerminalClientLifecycle.Closing &&
                next.Activity == TerminalClientActivity.None;
        }

        return current.Lifecycle switch
        {
            TerminalClientLifecycle.Disconnected =>
                next.Lifecycle == TerminalClientLifecycle.Connecting &&
                next.Activity == TerminalClientActivity.None,
            TerminalClientLifecycle.Connecting =>
                (next.Lifecycle == TerminalClientLifecycle.Authenticating ||
                next.Lifecycle == TerminalClientLifecycle.Blocked) &&
                next.Activity == TerminalClientActivity.None,
            TerminalClientLifecycle.Authenticating =>
                (next.Lifecycle == TerminalClientLifecycle.Ready ||
                next.Lifecycle == TerminalClientLifecycle.Blocked) &&
                next.Activity == TerminalClientActivity.None,
            TerminalClientLifecycle.Ready => IsReadyTransitionAllowed(current, next),
            _ => false,
        };
    }

    private static bool IsReadyTransitionAllowed(
        TerminalClientStateSnapshot current,
        TerminalClientStateSnapshot next)
    {
        if (next.Lifecycle == TerminalClientLifecycle.Blocked)
        {
            return next.Activity == TerminalClientActivity.None;
        }

        if (next.Lifecycle != TerminalClientLifecycle.Ready)
        {
            return false;
        }

        if (current.Activity == next.Activity)
        {
            return true;
        }

        return current.Activity switch
        {
            TerminalClientActivity.None => next.Activity is
                TerminalClientActivity.ResumingConversation or
                TerminalClientActivity.SelectingConversation or
                TerminalClientActivity.SendingTurn or
                TerminalClientActivity.AwaitingConfirmation or
                TerminalClientActivity.CompletingConversation,
            TerminalClientActivity.ResumingConversation => next.Activity == TerminalClientActivity.None,
            TerminalClientActivity.SelectingConversation => next.Activity is
                TerminalClientActivity.None or TerminalClientActivity.CompletingConversation,
            TerminalClientActivity.SendingTurn => next.Activity is
                TerminalClientActivity.None or TerminalClientActivity.AwaitingConfirmation,
            TerminalClientActivity.AwaitingConfirmation => next.Activity is
                TerminalClientActivity.None or TerminalClientActivity.ResolvingConfirmation,
            TerminalClientActivity.ResolvingConfirmation => next.Activity is
                TerminalClientActivity.None or TerminalClientActivity.AwaitingConfirmation,
            TerminalClientActivity.CompletingConversation => next.Activity == TerminalClientActivity.None,
            _ => false,
        };
    }

    private void Notify(TerminalClientStateSnapshot snapshot)
    {
        try
        {
            _sink.OnStateChanged(snapshot);
        }
        catch (Exception)
        {
            // State observers must not interrupt private-client operations.
        }
    }
}
