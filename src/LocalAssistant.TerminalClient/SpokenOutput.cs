using System.Text;

namespace LocalAssistant.TerminalClient;

/// <summary>
/// Prepares a displayed response for synthesis. The goal is a natural conversation, so
/// characters a TTS engine would read out by their Unicode name (emoji, pictographs,
/// symbols) are removed and the surrounding whitespace collapsed. The transcript still
/// shows the original text; only the spoken channel is filtered.
/// </summary>
internal static class SpokenText
{
    public static string ForSpeech(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var stripped = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            stripped.Append(IsSpeakable(rune) ? rune.ToString() : ' ');
        }

        return CollapseWhitespace(stripped.ToString());
    }

    private static bool IsSpeakable(Rune rune)
    {
        var value = rune.Value;
        if (value == '\n')
        {
            return true;
        }

        if (value < 0x20 || value == 0x7F || (value is >= 0x80 and <= 0x9F))
        {
            return false;
        }

        return value switch
        {
            >= 0x1F000 => false,               // emoji supplementary planes
            >= 0x2600 and <= 0x27BF => false,  // miscellaneous symbols and dingbats
            >= 0x2B00 and <= 0x2BFF => false,  // miscellaneous symbols and arrows
            >= 0xFE00 and <= 0xFE0F => false,  // variation selectors
            0x200D => false,                   // zero-width joiner
            0x20E3 => false,                   // combining enclosing keycap
            _ => true,
        };
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value)
        {
            if (character == '\n')
            {
                while (builder.Length > 0 && builder[^1] == ' ')
                {
                    builder.Length--;
                }

                builder.Append('\n');
                lastWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}

internal enum SpokenOutputAvailability
{
    Unavailable,
    Ready,
}

internal enum SpokenOutputProvider
{
    Sapi,
    Kokoro,
    None,
}

internal sealed record SpokenOutputPreferences
{
    public const int MinimumRate = -10;
    public const int MaximumRate = 10;
    public const int MinimumVolume = 0;
    public const int MaximumVolume = 100;

    public SpokenOutputPreferences(
        string? voiceId = null,
        int rate = 0,
        int volume = 100,
        bool isMuted = false,
        SpokenOutputProvider requestedProvider = SpokenOutputProvider.Sapi,
        string? kokoroProfileId = "jarvis-es",
        string kokoroLanguage = "es",
        int kokoroVolume = 100,
        bool useSapiFallback = true)
    {
        if (voiceId is not null && string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException("Voice identifier must not be empty.", nameof(voiceId));
        }

        if (rate < MinimumRate || rate > MaximumRate)
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }

        if (volume < MinimumVolume || volume > MaximumVolume)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        if (!Enum.IsDefined(requestedProvider) ||
            (kokoroProfileId is not null && string.IsNullOrWhiteSpace(kokoroProfileId)) ||
            kokoroLanguage is not ("es" or "en") ||
            kokoroVolume < MinimumVolume || kokoroVolume > MaximumVolume)
        {
            throw new ArgumentException("The Kokoro spoken-output preferences are invalid.");
        }

        VoiceId = voiceId;
        Rate = rate;
        Volume = volume;
        IsMuted = isMuted;
        RequestedProvider = requestedProvider;
        KokoroProfileId = kokoroProfileId;
        KokoroLanguage = kokoroLanguage;
        KokoroVolume = kokoroVolume;
        UseSapiFallback = useSapiFallback;
    }

    public string? VoiceId { get; }

    public int Rate { get; }

    public int Volume { get; }

    public bool IsMuted { get; }

    public SpokenOutputProvider RequestedProvider { get; }

    public string? KokoroProfileId { get; }

    public string KokoroLanguage { get; }

    public int KokoroVolume { get; }

    public bool UseSapiFallback { get; }

    public static SpokenOutputPreferences Default { get; } = new();

    public SpokenOutputPreferences WithRequestedProvider(SpokenOutputProvider provider) => new(
        VoiceId,
        Rate,
        Volume,
        IsMuted,
        provider,
        KokoroProfileId,
        KokoroLanguage,
        KokoroVolume,
        UseSapiFallback);

    public SpokenOutputPreferences WithSapiVoice(string? voiceId) => new(
        voiceId,
        Rate,
        Volume,
        IsMuted,
        RequestedProvider,
        KokoroProfileId,
        KokoroLanguage,
        KokoroVolume,
        UseSapiFallback);

    public SpokenOutputPreferences WithSapiRate(int rate) => new(
        VoiceId,
        rate,
        Volume,
        IsMuted,
        RequestedProvider,
        KokoroProfileId,
        KokoroLanguage,
        KokoroVolume,
        UseSapiFallback);

    public SpokenOutputPreferences WithSapiVolume(int volume) => new(
        VoiceId,
        Rate,
        volume,
        IsMuted,
        RequestedProvider,
        KokoroProfileId,
        KokoroLanguage,
        KokoroVolume,
        UseSapiFallback);

    public SpokenOutputPreferences WithMute(bool isMuted) => new(
        VoiceId,
        Rate,
        Volume,
        isMuted,
        RequestedProvider,
        KokoroProfileId,
        KokoroLanguage,
        KokoroVolume,
        UseSapiFallback);

    public SpokenOutputPreferences WithKokoroProfile(string profileId, string language) => new(
        VoiceId,
        Rate,
        Volume,
        IsMuted,
        RequestedProvider,
        profileId,
        language,
        KokoroVolume,
        UseSapiFallback);

    public SpokenOutputPreferences WithKokoroVolume(int volume) => new(
        VoiceId,
        Rate,
        Volume,
        IsMuted,
        RequestedProvider,
        KokoroProfileId,
        KokoroLanguage,
        volume,
        UseSapiFallback);

    /// <summary>
    /// Projects the voice/rate/volume that a snapshot should display for whichever
    /// provider is actually requested, instead of always showing the SAPI fields
    /// regardless of the current selection.
    /// </summary>
    public (string? VoiceId, int Rate, int Volume) ProjectDisplay() => RequestedProvider switch
    {
        SpokenOutputProvider.Kokoro => (NormalizeVoiceId(KokoroProfileId), 0, KokoroVolume),
        SpokenOutputProvider.None => (null, 0, Default.Volume),
        _ => (NormalizeVoiceId(VoiceId), Rate, Volume),
    };

    private static string? NormalizeVoiceId(string? voiceId) => string.IsNullOrWhiteSpace(voiceId)
        ? null
        : TerminalTextSanitizer.NormalizeSingleLine(voiceId);
}

internal sealed record TerminalClientSpokenOutputState(
    SpokenOutputAvailability Availability,
    bool IsMuted,
    string? VoiceId = null,
    int Rate = 0,
    int Volume = 100,
    string? WarningCode = null,
    SpokenOutputProvider RequestedProvider = SpokenOutputProvider.Sapi,
    SpokenOutputProvider? EffectiveProvider = null,
    bool UsedProviderFallback = false)
{
    public static TerminalClientSpokenOutputState Unavailable { get; } = new(
        SpokenOutputAvailability.Unavailable,
        false,
        null,
        0,
        100);
}

internal sealed record SpokenOutputVoice(
    string Id,
    SpokenOutputProvider Provider = SpokenOutputProvider.Sapi,
    string? Language = null);

internal sealed record SpeechSynthesisRequest(
    string Text,
    SpokenOutputPreferences Preferences);

internal sealed class SynthesizedSpeech : IAsyncDisposable
{
    private int _disposed;

    public SynthesizedSpeech(
        Stream content,
        string mediaType,
        string? effectiveVoiceId = null,
        bool usedVoiceFallback = false,
        SpokenOutputProvider effectiveProvider = SpokenOutputProvider.Sapi)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("Media type must not be empty.", nameof(mediaType));
        }

        MediaType = mediaType;
        EffectiveVoiceId = effectiveVoiceId;
        UsedVoiceFallback = usedVoiceFallback;
        EffectiveProvider = effectiveProvider;
    }

    public Stream Content { get; }

    public string MediaType { get; }

    public string? EffectiveVoiceId { get; }

    public bool UsedVoiceFallback { get; }

    public SpokenOutputProvider EffectiveProvider { get; }

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

internal interface ISpeechVoiceCatalog
{
    Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken);
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
}

internal sealed record SpokenOutputPreparation(
    SpokenOutputPreparationKind Kind,
    IPreparedSpokenOutput? PreparedOutput,
    KokoroClientFailureKind? KokoroFailure = null)
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

    public static SpokenOutputPreparation KokoroSynthesisFailed(KokoroClientFailureKind failure) => new(
        SpokenOutputPreparationKind.SynthesisFailed,
        null,
        failure);

    public static SpokenOutputPreparation Prepared(IPreparedSpokenOutput preparedOutput) => new(
        SpokenOutputPreparationKind.Prepared,
        preparedOutput ?? throw new ArgumentNullException(nameof(preparedOutput)));
}

internal enum SpokenOutputPlaybackKind
{
    Completed,
    PlaybackFailed,
    PartialSynthesisFailed,
    Cancelled,
}

internal sealed record SpokenOutputPlaybackResult(SpokenOutputPlaybackKind Kind)
{
    public static SpokenOutputPlaybackResult Completed { get; } = new(
        SpokenOutputPlaybackKind.Completed);

    public static SpokenOutputPlaybackResult PlaybackFailed { get; } = new(
        SpokenOutputPlaybackKind.PlaybackFailed);

    public static SpokenOutputPlaybackResult PartialSynthesisFailed { get; } = new(
        SpokenOutputPlaybackKind.PartialSynthesisFailed);

    public static SpokenOutputPlaybackResult Cancelled { get; } = new(
        SpokenOutputPlaybackKind.Cancelled);
}

internal enum SpokenOutputPlaybackStage
{
    Playing,
    Buffering,
}

internal interface IPreparedSpokenOutput : IAsyncDisposable
{
    event Action<SpokenOutputPlaybackStage>? StageChanged;

    Task<SpokenOutputPlaybackResult> PlayAsync(CancellationToken cancellationToken);
}

internal interface ISpokenOutputCoordinator : IAsyncDisposable
{
    TerminalClientSpokenOutputState State { get; }

    SpokenOutputPreferences RequestedPreferences { get; }

    Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies validated preferences to later operations. A command line is only
    /// processed by <c>TerminalClientApplication</c> while no operation is in flight, and
    /// an in-flight synthesis already captured its own immutable
    /// <see cref="SpeechSynthesisRequest"/>, so this never disturbs a prepared artifact.
    /// </summary>
    void UpdatePreferences(SpokenOutputPreferences preferences);

    Task StopAsync(CancellationToken cancellationToken);

    Task<SpokenOutputPreparation> PrepareAsync(string text, CancellationToken cancellationToken);
}

internal sealed class SpokenOutputCoordinator : ISpokenOutputCoordinator
{
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly ISpeechPlayer _player;
    private SpokenOutputPreferences _preferences;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _playbackSync = new();
    private CancellationTokenSource? _playbackCancellation;
    private Task? _activePlayback;

    /// <summary>
    /// Signals every operation belonging to the current prepared output, including a
    /// segmented pipeline's prefetch synthesis, so that <see cref="StopAsync"/> reaches
    /// work that is not the single clip currently playing.
    /// </summary>
    private CancellationTokenSource? _stopSignal;
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
        var display = preferences.ProjectDisplay();
        State = new TerminalClientSpokenOutputState(
            availability,
            preferences.IsMuted,
            display.VoiceId,
            display.Rate,
            display.Volume,
            RequestedProvider: preferences.RequestedProvider);
    }

    public TerminalClientSpokenOutputState State { get; private set; }

    public SpokenOutputPreferences RequestedPreferences => _preferences;

    public async Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        if (_synthesizer is not ISpeechVoiceCatalog catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }

        try
        {
            return await catalog.GetVoicesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A mid-session enumeration failure degrades to "no voices" rather than
            // terminating the client from a /voice command or preference load.
            return [];
        }
    }

    public void UpdatePreferences(SpokenOutputPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences;
        var display = preferences.ProjectDisplay();
        State = new TerminalClientSpokenOutputState(
            State.Availability,
            preferences.IsMuted,
            display.VoiceId,
            display.Rate,
            display.Volume,
            WarningCode: null,
            RequestedProvider: preferences.RequestedProvider,
            EffectiveProvider: null,
            UsedProviderFallback: false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? playback;
        lock (_playbackSync)
        {
            _stopSignal?.Cancel();
            _playbackCancellation?.Cancel();
            playback = _activePlayback;
        }

        if (playback is null)
        {
            return;
        }

        try
        {
            await playback;
        }
        catch (OperationCanceledException)
        {
            // Local stop is an expected playback outcome.
        }
    }

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

        if (_preferences.RequestedProvider == SpokenOutputProvider.None)
        {
            // The user explicitly disabled spoken output; this is not a synthesis
            // failure and must never surface an error.
            return SpokenOutputPreparation.Muted;
        }

        await _operationGate.WaitAsync(cancellationToken);
        var stopSignal = new CancellationTokenSource();
        lock (_playbackSync)
        {
            _stopSignal?.Dispose();
            _stopSignal = stopSignal;
        }

        try
        {
            var selector = _synthesizer as ProviderSelectingSpeechSynthesizer;
            var kokoroSynthesizer = selector?.KokoroSynthesizer ?? _synthesizer as KokoroSpeechSynthesizer;
            if (_preferences.RequestedProvider == SpokenOutputProvider.Kokoro &&
                kokoroSynthesizer is not null)
            {
                try
                {
                    return await PrepareKokoroSegmentsAsync(
                        text,
                        kokoroSynthesizer,
                        stopSignal.Token,
                        cancellationToken);
                }
                catch (KokoroSpeechException) when (_preferences.UseSapiFallback && selector is not null)
                {
                    var fallback = await selector.SynthesizeWithSapiAsync(
                        new SpeechSynthesisRequest(text, _preferences),
                        cancellationToken);
                    State = State with
                    {
                        VoiceId = NormalizeVoiceId(fallback.EffectiveVoiceId ?? _preferences.VoiceId),
                        WarningCode = null,
                        RequestedProvider = _preferences.RequestedProvider,
                        EffectiveProvider = fallback.EffectiveProvider,
                        UsedProviderFallback = true,
                    };
                    return SpokenOutputPreparation.Prepared(new PreparedSpokenOutput(
                        fallback,
                        _operationGate,
                        PlayAsync));
                }
            }

            var speech = await _synthesizer.SynthesizeAsync(
                new SpeechSynthesisRequest(text, _preferences),
                cancellationToken);
            State = State with
            {
                VoiceId = NormalizeVoiceId(speech.EffectiveVoiceId ?? _preferences.VoiceId),
                WarningCode = speech.UsedVoiceFallback ? "speech_voice_unavailable" : null,
                RequestedProvider = _preferences.RequestedProvider,
                EffectiveProvider = speech.EffectiveProvider,
                // A provider fallback (e.g. Kokoro -> SAPI) is a different event from
                // SAPI substituting a voice it could find; only the former belongs here.
                UsedProviderFallback = _preferences.RequestedProvider != speech.EffectiveProvider,
            };
            return SpokenOutputPreparation.Prepared(new PreparedSpokenOutput(
                speech,
                _operationGate,
                PlayAsync));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _operationGate.Release();
            throw;
        }
        catch (KokoroSpeechException exception)
        {
            _operationGate.Release();
            return SpokenOutputPreparation.KokoroSynthesisFailed(exception.Failure);
        }
        catch (Exception)
        {
            // Any other failure, including an unexpected cancellation whose source is not
            // the application token, is a local synthesis failure that degrades to text.
            _operationGate.Release();
            return SpokenOutputPreparation.SynthesisFailed;
        }
    }

    private async Task<SpokenOutputPreparation> PrepareKokoroSegmentsAsync(
        string text,
        KokoroSpeechSynthesizer synthesizer,
        CancellationToken stopToken,
        CancellationToken cancellationToken)
    {
        var segments = SpokenTextSegmenter.Segment(text).ToArray();
        var first = await synthesizer.SynthesizeAsync(
            new SpeechSynthesisRequest(segments[0], _preferences),
            cancellationToken);
        State = State with
        {
            VoiceId = NormalizeVoiceId(first.EffectiveVoiceId ?? _preferences.KokoroProfileId),
            WarningCode = null,
            RequestedProvider = _preferences.RequestedProvider,
            EffectiveProvider = first.EffectiveProvider,
            UsedProviderFallback = false,
        };
        return SpokenOutputPreparation.Prepared(new SegmentedPreparedSpokenOutput(
            first,
            segments,
            synthesizer,
            _player,
            _operationGate,
            PlayAsync,
            _preferences,
            stopToken));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_playbackSync)
            {
                _playbackCancellation?.Cancel();
                _stopSignal?.Cancel();
                _stopSignal?.Dispose();
            }

            _operationGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SpokenOutputPlaybackResult> PlayAsync(
        SynthesizedSpeech speech,
        CancellationToken cancellationToken)
    {
        using var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task playback;
        lock (_playbackSync)
        {
            _playbackCancellation = localCancellation;
            playback = _player.PlayAsync(speech, localCancellation.Token);
            _activePlayback = playback;
        }

        try
        {
            await playback;
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
        finally
        {
            lock (_playbackSync)
            {
                if (ReferenceEquals(_playbackCancellation, localCancellation))
                {
                    _playbackCancellation = null;
                    _activePlayback = null;
                }
            }
        }
    }

    private static string? NormalizeVoiceId(string? voiceId) => string.IsNullOrWhiteSpace(voiceId)
        ? null
        : TerminalTextSanitizer.NormalizeSingleLine(voiceId);

    private sealed class PreparedSpokenOutput : IPreparedSpokenOutput
    {
        private readonly SynthesizedSpeech _speech;
        private readonly SemaphoreSlim _operationGate;
        private readonly Func<SynthesizedSpeech, CancellationToken, Task<SpokenOutputPlaybackResult>> _play;
        private int _disposed;

        public PreparedSpokenOutput(
            SynthesizedSpeech speech,
            SemaphoreSlim operationGate,
            Func<SynthesizedSpeech, CancellationToken, Task<SpokenOutputPlaybackResult>> play)
        {
            _speech = speech ?? throw new ArgumentNullException(nameof(speech));
            _operationGate = operationGate ?? throw new ArgumentNullException(nameof(operationGate));
            _play = play ?? throw new ArgumentNullException(nameof(play));
        }

        public async Task<SpokenOutputPlaybackResult> PlayAsync(CancellationToken cancellationToken)
        {
            StageChanged?.Invoke(SpokenOutputPlaybackStage.Playing);
            return await _play(_speech, cancellationToken);
        }

        public event Action<SpokenOutputPlaybackStage>? StageChanged;

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

    private sealed class SegmentedPreparedSpokenOutput : IPreparedSpokenOutput
    {
        private readonly string[] _segments;
        private readonly ISpeechSynthesizer _synthesizer;
        private readonly ISpeechPlayer _player;
        private readonly SemaphoreSlim _operationGate;
        private readonly Func<SynthesizedSpeech, CancellationToken, Task<SpokenOutputPlaybackResult>> _play;
        private readonly SpokenOutputPreferences _preferences;
        private readonly CancellationToken _stopToken;
        private SynthesizedSpeech? _first;
        private int _disposed;

        public SegmentedPreparedSpokenOutput(
            SynthesizedSpeech first,
            string[] segments,
            ISpeechSynthesizer synthesizer,
            ISpeechPlayer player,
            SemaphoreSlim operationGate,
            Func<SynthesizedSpeech, CancellationToken, Task<SpokenOutputPlaybackResult>> play,
            SpokenOutputPreferences preferences,
            CancellationToken stopToken)
        {
            _first = first;
            _segments = segments;
            _synthesizer = synthesizer;
            _player = player;
            _operationGate = operationGate;
            _play = play;
            _preferences = preferences;
            _stopToken = stopToken;
        }

        public async Task<SpokenOutputPlaybackResult> PlayAsync(CancellationToken cancellationToken)
        {
            var current = _first ?? throw new ObjectDisposedException(nameof(SegmentedPreparedSpokenOutput));
            _first = null;
            for (var index = 0; index < _segments.Length; index++)
            {
                // Prefetch shares the stop signal so /stop reaches it even though the
                // outer cancellationToken (an application-level token) does not.
                using var prefetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _stopToken);
                Task<SynthesizedSpeech>? next = index + 1 < _segments.Length
                    ? _synthesizer.SynthesizeAsync(
                        new SpeechSynthesisRequest(_segments[index + 1], _preferences),
                        prefetchCancellation.Token)
                    : null;
                try
                {
                    SpokenOutputPlaybackResult played;
                    try
                    {
                        StageChanged?.Invoke(SpokenOutputPlaybackStage.Playing);
                        played = await _play(current, cancellationToken);
                    }
                    finally
                    {
                        await current.DisposeAsync();
                    }

                    if (played.Kind != SpokenOutputPlaybackKind.Completed)
                    {
                        return played;
                    }

                    if (next is null)
                    {
                        return SpokenOutputPlaybackResult.Completed;
                    }

                    if (!next.IsCompleted)
                    {
                        StageChanged?.Invoke(SpokenOutputPlaybackStage.Buffering);
                    }

                    current = await next;
                    next = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // The pipeline's own stop signal, not the caller's token: a graceful
                    // stop, not a failure.
                    return SpokenOutputPlaybackResult.Cancelled;
                }
                catch (Exception)
                {
                    return SpokenOutputPlaybackResult.PartialSynthesisFailed;
                }
                finally
                {
                    // Whatever outcome this iteration reached, a prefetch that was started
                    // must always be observed and its artifact released; otherwise a
                    // /stop or a failure elsewhere in the segment leaves a synthesis
                    // running unobserved and its WAV never disposed. Cancelling first
                    // (harmless if `next` is null or already consumed) means a genuine
                    // playback failure reports promptly instead of waiting behind the
                    // full synthesis timeout for a segment nobody will play.
                    prefetchCancellation.Cancel();
                    await DisposeNextAsync(next);
                }
            }

            return SpokenOutputPlaybackResult.Completed;
        }

        public event Action<SpokenOutputPlaybackStage>? StageChanged;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                if (_first is not null)
                {
                    await _first.DisposeAsync();
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private static async Task DisposeNextAsync(Task<SynthesizedSpeech>? pending)
        {
            if (pending is null)
            {
                return;
            }

            try
            {
                var speech = await pending.ConfigureAwait(false);
                await speech.DisposeAsync();
            }
            catch
            {
                // The turn already has a terminal result; a cancelled or failed prefetch
                // has nothing further to report and must never surface as an unobserved
                // task exception.
            }
        }
    }
}

internal sealed class UnavailableSpokenOutputCoordinator : ISpokenOutputCoordinator
{
    public UnavailableSpokenOutputCoordinator(SpokenOutputPreferences? preferences = null)
    {
        var effectivePreferences = preferences ?? SpokenOutputPreferences.Default;
        RequestedPreferences = effectivePreferences;
        var display = effectivePreferences.ProjectDisplay();
        State = new TerminalClientSpokenOutputState(
            SpokenOutputAvailability.Unavailable,
            effectivePreferences.IsMuted,
            display.VoiceId,
            display.Rate,
            display.Volume,
            RequestedProvider: effectivePreferences.RequestedProvider);
    }

    public TerminalClientSpokenOutputState State { get; private set; }

    public SpokenOutputPreferences RequestedPreferences { get; private set; }

    public Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SpokenOutputVoice>>([]);
    }

    public void UpdatePreferences(SpokenOutputPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        RequestedPreferences = preferences;
        var display = preferences.ProjectDisplay();
        State = new TerminalClientSpokenOutputState(
            SpokenOutputAvailability.Unavailable,
            preferences.IsMuted,
            display.VoiceId,
            display.Rate,
            display.Volume,
            WarningCode: null,
            RequestedProvider: preferences.RequestedProvider,
            EffectiveProvider: null,
            UsedProviderFallback: false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<SpokenOutputPreparation> PrepareAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpokenOutputPreparation.Unavailable);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
