using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientStateTests
{
    [Fact]
    public void CoordinatorPublishesInitialStateAndSuppressesDuplicateSnapshots()
    {
        var sink = new RecordingStateSink();
        var coordinator = new TerminalClientStateCoordinator(sink);

        Assert.True(coordinator.PublishInitial());
        Assert.False(coordinator.PublishInitial());
        Assert.True(coordinator.TryTransition(Connecting()));
        Assert.True(coordinator.TryTransition(Connecting()));

        Assert.Collection(
            sink.Snapshots,
            snapshot => Assert.Equal(TerminalClientLifecycle.Disconnected, snapshot.Lifecycle),
            snapshot => Assert.Equal(TerminalClientLifecycle.Connecting, snapshot.Lifecycle));
    }

    [Fact]
    public void CoordinatorRejectsInvalidTransitionWithoutChangingOrPublishingState()
    {
        var sink = new RecordingStateSink();
        var coordinator = new TerminalClientStateCoordinator(sink);
        coordinator.PublishInitial();

        var invalid = TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
        };

        Assert.False(coordinator.TryTransition(invalid));
        Assert.Equal(TerminalClientStateSnapshot.Initial, coordinator.Current);
        Assert.Single(sink.Snapshots);
    }

    [Fact]
    public void CoordinatorAllowsObservableProviderAndConversationChangesWhileReady()
    {
        var sink = new RecordingStateSink();
        var coordinator = new TerminalClientStateCoordinator(sink);
        var conversationId = Guid.Parse("6ee6c630-43c8-4a02-841a-2118460c0a50");

        coordinator.PublishInitial();
        Assert.True(coordinator.TryTransition(Connecting()));
        Assert.True(coordinator.TryTransition(Authenticating()));
        Assert.True(coordinator.TryTransition(Ready("fake")));
        Assert.True(coordinator.TryTransition(Ready("ollama") with
        {
            ConversationId = conversationId,
        }));

        var current = Assert.IsType<TerminalClientStateSnapshot>(coordinator.Current);
        Assert.Equal("ollama", current.Provider);
        Assert.Equal(conversationId, current.ConversationId);
        Assert.Null(current.PendingConfirmation);
        Assert.Equal(5, sink.Snapshots.Count);
    }

    [Fact]
    public void CoordinatorRejectsInternallyIncoherentSnapshots()
    {
        var invalidSnapshots = new[]
        {
            Ready(null),
            Ready("fake") with
            {
                PendingConfirmation = PendingConfirmation(),
            },
            Ready("fake") with
            {
                Activity = TerminalClientActivity.AwaitingConfirmation,
            },
            Ready("fake") with
            {
                Lifecycle = TerminalClientLifecycle.Blocked,
            },
            Ready("fake") with
            {
                Lifecycle = TerminalClientLifecycle.Blocked,
                Error = new TerminalClientOperationError(
                    TerminalClientErrorCategory.Recoverable,
                    "recoverable",
                    "A recoverable error.",
                    "test"),
            },
            Connecting() with
            {
                ConversationId = Guid.Parse("057401ec-6ea1-4f6a-bf99-942a76f06417"),
            },
            Connecting() with
            {
                PendingConfirmation = PendingConfirmation(),
            },
        };

        foreach (var next in invalidSnapshots)
        {
            var sink = new RecordingStateSink();
            var coordinator = new TerminalClientStateCoordinator(sink);
            coordinator.PublishInitial();
            coordinator.TryTransition(Connecting());
            coordinator.TryTransition(Authenticating());
            coordinator.TryTransition(Ready("fake"));

            Assert.False(coordinator.TryTransition(next));
            Assert.Equal(Ready("fake"), coordinator.Current);
            Assert.Equal(4, sink.Snapshots.Count);
        }
    }

    [Fact]
    public void CoordinatorDoesNotAllowPlayingVoiceInThisIncrement()
    {
        var coordinator = new TerminalClientStateCoordinator(NullTerminalClientStateSink.Instance);
        coordinator.PublishInitial();
        coordinator.TryTransition(Connecting());
        coordinator.TryTransition(Authenticating());
        coordinator.TryTransition(Ready("fake"));

        var next = Ready("fake") with
        {
            Activity = TerminalClientActivity.PlayingVoice,
        };

        Assert.False(coordinator.TryTransition(next));
        Assert.Equal(TerminalClientActivity.None, coordinator.Current.Activity);
    }

    [Fact]
    public void SinkFailureDoesNotPreventStateTransition()
    {
        var coordinator = new TerminalClientStateCoordinator(new ThrowingStateSink());

        Assert.True(coordinator.PublishInitial());
        Assert.True(coordinator.TryTransition(Connecting()));
        Assert.Equal(TerminalClientLifecycle.Connecting, coordinator.Current.Lifecycle);
    }

    [Fact]
    public void CoordinatorRequiresClosingBeforeClosed()
    {
        var coordinator = new TerminalClientStateCoordinator(NullTerminalClientStateSink.Instance);
        coordinator.PublishInitial();
        coordinator.TryTransition(Connecting());

        var closed = Connecting() with
        {
            Lifecycle = TerminalClientLifecycle.Closed,
        };

        Assert.False(coordinator.TryTransition(closed));
        Assert.True(coordinator.TryTransition(Connecting() with
        {
            Lifecycle = TerminalClientLifecycle.Closing,
        }));
        Assert.True(coordinator.TryTransition(Connecting() with
        {
            Lifecycle = TerminalClientLifecycle.Closed,
        }));
    }

    [Fact]
    public void SnapshotContractDoesNotExposeSecretsOrConversationContent()
    {
        var snapshotProperties = typeof(TerminalClientStateSnapshot)
            .GetProperties()
            .Select(property => property.Name);
        var confirmationProperties = typeof(TerminalClientPendingConfirmation)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain(snapshotProperties, name => name.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshotProperties, name => name.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshotProperties, name => name.Contains("challenge", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshotProperties, name => name.Contains("content", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(confirmationProperties, name =>
            name.Contains("argument", StringComparison.OrdinalIgnoreCase));
    }

    private static TerminalClientStateSnapshot Connecting() => TerminalClientStateSnapshot.Initial with
    {
        Lifecycle = TerminalClientLifecycle.Connecting,
    };

    private static TerminalClientStateSnapshot Authenticating() => TerminalClientStateSnapshot.Initial with
    {
        Lifecycle = TerminalClientLifecycle.Authenticating,
    };

    private static TerminalClientStateSnapshot Ready(string? provider) => TerminalClientStateSnapshot.Initial with
    {
        Lifecycle = TerminalClientLifecycle.Ready,
        Provider = provider,
    };

    private static TerminalClientPendingConfirmation PendingConfirmation() => new(
        "create_reminder",
        new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));

    private sealed class RecordingStateSink : ITerminalClientStateSink
    {
        public List<TerminalClientStateSnapshot> Snapshots { get; } = [];

        public void OnStateChanged(TerminalClientStateSnapshot snapshot) => Snapshots.Add(snapshot);
    }

    private sealed class ThrowingStateSink : ITerminalClientStateSink
    {
        public void OnStateChanged(TerminalClientStateSnapshot snapshot) =>
            throw new InvalidOperationException("Observer failure.");
    }
}
