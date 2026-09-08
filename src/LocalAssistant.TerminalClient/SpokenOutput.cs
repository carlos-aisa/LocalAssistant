namespace LocalAssistant.TerminalClient;

internal enum SpokenOutputAvailability
{
    Unavailable,
    Ready,
}

internal sealed record SpokenOutputPreferences(bool IsMuted)
{
    public static SpokenOutputPreferences Default { get; } = new(false);
}

internal sealed record TerminalClientSpokenOutputState(
    SpokenOutputAvailability Availability,
    bool IsMuted)
{
    public static TerminalClientSpokenOutputState Unavailable { get; } = new(
        SpokenOutputAvailability.Unavailable,
        false);
}

internal sealed record SpeechSynthesisRequest(
    string Text,
    SpokenOutputPreferences Preferences);

internal sealed class SynthesizedSpeech : IAsyncDisposable
{
    private int _disposed;

    public SynthesizedSpeech(Stream content, string mediaType)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("Media type must not be empty.", nameof(mediaType));
        }

        MediaType = mediaType;
    }

    public Stream Content { get; }

    public string MediaType { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await Content.DisposeAsync();
    }
}

internal interface ISpeechSynthesizer
{
    Task<SynthesizedSpeech> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken);
}

internal interface ISpeechPlayer
{
    Task PlayAsync(SynthesizedSpeech speech, CancellationToken cancellationToken);
}

internal enum SpokenOutputPreparationKind
{
    Unavailable,
    Muted,
    Prepared,
    SynthesisFailed,
    Cancelled,
}

internal sealed record SpokenOutputPreparation(
    SpokenOutputPreparationKind Kind,
    IPreparedSpokenOutput? PreparedOutput)
{
    public static SpokenOutputPreparation Unavailable { get; } = new(
        SpokenOutputPreparationKind.Unavailable,
        null);

    public static SpokenOutputPreparation Muted { get; } = new(
        SpokenOutputPreparationKind.Muted,
        null);

    public static SpokenOutputPreparation SynthesisFailed { get; } = new(
        SpokenOutputPreparationKind.SynthesisFailed,
        null);

    public static SpokenOutputPreparation Cancelled { get; } = new(
        SpokenOutputPreparationKind.Cancelled,
        null);

    public static SpokenOutputPreparation Prepared(IPreparedSpokenOutput preparedOutput) => new(
        SpokenOutputPreparationKind.Prepared,
        preparedOutput ?? throw new ArgumentNullException(nameof(preparedOutput)));
}

internal enum SpokenOutputPlaybackKind
{
    Completed,
    PlaybackFailed,
    Cancelled,
}

internal sealed record SpokenOutputPlaybackResult(SpokenOutputPlaybackKind Kind)
{
    public static SpokenOutputPlaybackResult Completed { get; } = new(
        SpokenOutputPlaybackKind.Completed);

    public static SpokenOutputPlaybackResult PlaybackFailed { get; } = new(
        SpokenOutputPlaybackKind.PlaybackFailed);

    public static SpokenOutputPlaybackResult Cancelled { get; } = new(
        SpokenOutputPlaybackKind.Cancelled);
}

internal interface IPreparedSpokenOutput : IAsyncDisposable
{
    Task<SpokenOutputPlaybackResult> PlayAsync(CancellationToken cancellationToken);
}

internal interface ISpokenOutputCoordinator : IAsyncDisposable
{
    TerminalClientSpokenOutputState State { get; }

    Task<SpokenOutputPreparation> PrepareAsync(string text, CancellationToken cancellationToken);
}

internal sealed class SpokenOutputCoordinator : ISpokenOutputCoordinator
{
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ISpeechPlayer _player;
    private readonly SpokenOutputPreferences _preferences;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposed;

    public SpokenOutputCoordinator(
        ISpeechSynthesizer synthesizer,
        ISpeechPlayer player,
        SpokenOutputAvailability availability,
        SpokenOutputPreferences preferences)
    {
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        State = new TerminalClientSpokenOutputState(availability, preferences.IsMuted);
    }

    public TerminalClientSpokenOutputState State { get; }

    public async Task<SpokenOutputPreparation> PrepareAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (State.Availability == SpokenOutputAvailability.Unavailable)
        {
            return SpokenOutputPreparation.Unavailable;
        }

        if (State.IsMuted)
        {
            return SpokenOutputPreparation.Muted;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var speech = await _synthesizer.SynthesizeAsync(
                new SpeechSynthesisRequest(text, _preferences),
                cancellationToken);
            return SpokenOutputPreparation.Prepared(new PreparedSpokenOutput(
                speech,
                _player,
                _operationGate));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _operationGate.Release();
            throw;
        }
        catch (OperationCanceledException)
        {
            _operationGate.Release();
            return SpokenOutputPreparation.Cancelled;
        }
        catch (Exception)
        {
            _operationGate.Release();
            return SpokenOutputPreparation.SynthesisFailed;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class PreparedSpokenOutput : IPreparedSpokenOutput
    {
        private readonly SynthesizedSpeech _speech;
        private readonly ISpeechPlayer _player;
        private readonly SemaphoreSlim _operationGate;
        private int _disposed;

        public PreparedSpokenOutput(
            SynthesizedSpeech speech,
            ISpeechPlayer player,
            SemaphoreSlim operationGate)
        {
            _speech = speech ?? throw new ArgumentNullException(nameof(speech));
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
        }

        public async Task<SpokenOutputPlaybackResult> PlayAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _player.PlayAsync(_speech, cancellationToken);
                return SpokenOutputPlaybackResult.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return SpokenOutputPlaybackResult.Cancelled;
            }
            catch (Exception)
            {
                return SpokenOutputPlaybackResult.PlaybackFailed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await _speech.DisposeAsync();
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }
}

internal sealed class UnavailableSpokenOutputCoordinator : ISpokenOutputCoordinator
{
    public UnavailableSpokenOutputCoordinator(SpokenOutputPreferences? preferences = null)
    {
        var effectivePreferences = preferences ?? SpokenOutputPreferences.Default;
        State = new TerminalClientSpokenOutputState(
            SpokenOutputAvailability.Unavailable,
            effectivePreferences.IsMuted);
    }

    public TerminalClientSpokenOutputState State { get; }

    public Task<SpokenOutputPreparation> PrepareAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpokenOutputPreparation.Unavailable);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
