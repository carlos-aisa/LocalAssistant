using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class SpokenOutputTests
{
    [Fact]
    public async Task UnavailableOutputDoesNotInvokeSynthesisOrPlayback()
    {
        var synthesizer = new RecordingSynthesizer();
        var player = new RecordingPlayer();
        await using var coordinator = new UnavailableSpokenOutputCoordinator();

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.Unavailable, preparation.Kind);
        Assert.Equal(0, synthesizer.CallCount);
        Assert.Equal(0, player.CallCount);
    }

    [Fact]
    public async Task MutedOutputDoesNotInvokeSynthesisOrPlayback()
    {
        var synthesizer = new RecordingSynthesizer();
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(
            synthesizer,
            player,
            new SpokenOutputPreferences(IsMuted: true));

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.Muted, preparation.Kind);
        Assert.Equal(0, synthesizer.CallCount);
        Assert.Equal(0, player.CallCount);
    }

    [Fact]
    public async Task PreparedOutputSynthesizesPlaysAndDisposesItsArtifact()
    {
        var stream = new TrackingStream();
        var synthesizer = new RecordingSynthesizer(() => stream);
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(synthesizer, player);

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);
        await using (prepared)
        {
            var playback = await prepared.PlayAsync(CancellationToken.None);
            Assert.Equal(SpokenOutputPlaybackKind.Completed, playback.Kind);
        }

        Assert.Equal(1, synthesizer.CallCount);
        Assert.Equal("Final response.", synthesizer.Requests.Single().Text);
        Assert.Equal(1, player.CallCount);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task ASecondPreparationWaitsUntilTheFirstPreparedOutputIsReleased()
    {
        var synthesizer = new RecordingSynthesizer();
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(synthesizer, player);

        var first = await coordinator.PrepareAsync("First response.", CancellationToken.None);
        var firstPrepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(first.PreparedOutput);
        var secondTask = coordinator.PrepareAsync("Second response.", CancellationToken.None);

        Assert.False(secondTask.IsCompleted);

        await firstPrepared.DisposeAsync();
        var second = await secondTask;
        var secondPrepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(second.PreparedOutput);
        await secondPrepared.DisposeAsync();

        Assert.Equal(2, synthesizer.CallCount);
    }

    [Fact]
    public async Task AWaitingPreparationCanBeCancelledWithoutReleasingTheActiveArtifact()
    {
        var synthesizer = new RecordingSynthesizer();
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(synthesizer, player);
        using var cancellationSource = new CancellationTokenSource();

        var first = await coordinator.PrepareAsync("First response.", CancellationToken.None);
        var firstPrepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(first.PreparedOutput);
        var waiting = coordinator.PrepareAsync("Second response.", cancellationSource.Token);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await firstPrepared.DisposeAsync();

        var recovered = await coordinator.PrepareAsync("Third response.", CancellationToken.None);
        var recoveredPrepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(recovered.PreparedOutput);
        await recoveredPrepared.DisposeAsync();
    }

    [Fact]
    public async Task SynthesisFailureDoesNotInvokePlayback()
    {
        var synthesizer = new RecordingSynthesizer(exception: new InvalidOperationException("Failure."));
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(synthesizer, player);

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.SynthesisFailed, preparation.Kind);
        Assert.Equal(0, player.CallCount);
    }

    [Fact]
    public async Task PlaybackFailureDisposesTheArtifact()
    {
        var stream = new TrackingStream();
        var synthesizer = new RecordingSynthesizer(() => stream);
        var player = new RecordingPlayer(exception: new InvalidOperationException("Failure."));
        await using var coordinator = CreateCoordinator(synthesizer, player);

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);
        await using (prepared)
        {
            var playback = await prepared.PlayAsync(CancellationToken.None);
            Assert.Equal(SpokenOutputPlaybackKind.PlaybackFailed, playback.Kind);
        }

        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task DisposingPreparedOutputTwiceDisposesTheArtifactExactlyOnce()
    {
        var stream = new TrackingStream();
        var synthesizer = new RecordingSynthesizer(() => stream);
        await using var coordinator = CreateCoordinator(synthesizer, new RecordingPlayer());

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);

        await prepared.DisposeAsync();
        await prepared.DisposeAsync();

        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task ApplicationCancellationDuringSynthesisPropagatesAndReleasesTheGate()
    {
        var synthesizer = new RecordingSynthesizer(blockUntilCancellation: true);
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(synthesizer, player);
        using var cancellationSource = new CancellationTokenSource();

        var preparation = coordinator.PrepareAsync("Final response.", cancellationSource.Token);
        await synthesizer.WaitForCallAsync();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);

        var recovered = await coordinator.PrepareAsync("Next response.", CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(recovered.PreparedOutput);
        await prepared.DisposeAsync();
    }

    [Fact]
    public async Task ApplicationCancellationDuringPlaybackPropagatesAndDisposesTheArtifact()
    {
        var stream = new TrackingStream();
        var synthesizer = new RecordingSynthesizer(() => stream);
        var player = new RecordingPlayer(blockUntilCancellation: true);
        await using var coordinator = CreateCoordinator(synthesizer, player);
        using var cancellationSource = new CancellationTokenSource();

        var preparation = await coordinator.PrepareAsync("Final response.", cancellationSource.Token);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);
        await using (prepared)
        {
            var playback = prepared.PlayAsync(cancellationSource.Token);
            await player.WaitForCallAsync();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        }

        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public void SpokenOutputContractsDoNotExposeAudioContentOrProviderDetails()
    {
        var exposedProperties = new[]
        {
            typeof(TerminalClientSpokenOutputState),
            typeof(SpokenOutputPreparation),
            typeof(SpokenOutputPlaybackResult),
        }
        .SelectMany(type => type.GetProperties())
        .Select(property => property.Name);

        Assert.DoesNotContain(exposedProperties, name =>
            name.Contains("text", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exposedProperties, name =>
            name.Contains("audio", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exposedProperties, name =>
            name.Contains("path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exposedProperties, name =>
            name.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    private static SpokenOutputCoordinator CreateCoordinator(
        ISpeechSynthesizer synthesizer,
        ISpeechPlayer player,
        SpokenOutputPreferences? preferences = null) => new(
        synthesizer,
        player,
        SpokenOutputAvailability.Ready,
        preferences ?? SpokenOutputPreferences.Default);

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        private readonly Func<TrackingStream> _streamFactory;
        private readonly Exception? _exception;
        private readonly bool _blockUntilCancellation;
        private readonly TaskCompletionSource<bool> _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingSynthesizer(
            Func<TrackingStream>? streamFactory = null,
            Exception? exception = null,
            bool blockUntilCancellation = false)
        {
            _streamFactory = streamFactory ?? (() => new TrackingStream());
            _exception = exception;
            _blockUntilCancellation = blockUntilCancellation;
        }

        public int CallCount { get; private set; }

        public List<SpeechSynthesisRequest> Requests { get; } = [];

        public async Task<SynthesizedSpeech> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Requests.Add(request);
            _called.TrySetResult(true);

            if (_blockUntilCancellation && CallCount == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return new SynthesizedSpeech(_streamFactory(), "audio/test");
        }

        public Task<bool> WaitForCallAsync() => _called.Task;
    }

    private sealed class RecordingPlayer : ISpeechPlayer
    {
        private readonly Exception? _exception;
        private readonly bool _blockUntilCancellation;
        private readonly TaskCompletionSource<bool> _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingPlayer(Exception? exception = null, bool blockUntilCancellation = false)
        {
            _exception = exception;
            _blockUntilCancellation = blockUntilCancellation;
        }

        public int CallCount { get; private set; }

        public async Task PlayAsync(SynthesizedSpeech speech, CancellationToken cancellationToken)
        {
            CallCount++;
            _called.TrySetResult(true);

            if (_blockUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }
        }

        public Task<bool> WaitForCallAsync() => _called.Task;
    }

    private sealed class TrackingStream : MemoryStream
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
