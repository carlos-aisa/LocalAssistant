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
        bool isMuted = false)
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

        VoiceId = voiceId;
        Rate = rate;
        Volume = volume;
        IsMuted = isMuted;
    }

    public string? VoiceId { get; }

    public int Rate { get; }

    public int Volume { get; }

    public bool IsMuted { get; }

    public static SpokenOutputPreferences Default { get; } = new();
}

internal sealed record TerminalClientSpokenOutputState(
    SpokenOutputAvailability Availability,
    bool IsMuted,
    string? VoiceId = null,
    int Rate = 0,
    int Volume = 100,
    string? WarningCode = null)
{
    public static TerminalClientSpokenOutputState Unavailable { get; } = new(
        SpokenOutputAvailability.Unavailable,
        false,
        null,
        0,
        100);
}

internal sealed record SpokenOutputVoice(string Id);

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
        bool usedVoiceFallback = false)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("Media type must not be empty.", nameof(mediaType));
        }

        MediaType = mediaType;
        EffectiveVoiceId = effectiveVoiceId;
        UsedVoiceFallback = usedVoiceFallback;
    }

    public Stream Content { get; }

    public string MediaType { get; }

    public string? EffectiveVoiceId { get; }

    public bool UsedVoiceFallback { get; }

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
        State = new TerminalClientSpokenOutputState(
            availability,
            preferences.IsMuted,
            NormalizeVoiceId(preferences.VoiceId),
            preferences.Rate,
            preferences.Volume);
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
        State = new TerminalClientSpokenOutputState(
            State.Availability,
            preferences.IsMuted,
            NormalizeVoiceId(preferences.VoiceId),
            preferences.Rate,
            preferences.Volume,
            WarningCode: null);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? playback;
        lock (_playbackSync)
        {
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

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var speech = await _synthesizer.SynthesizeAsync(
                new SpeechSynthesisRequest(text, _preferences),
                cancellationToken);
            State = State with
            {
                VoiceId = NormalizeVoiceId(speech.EffectiveVoiceId ?? _preferences.VoiceId),
                WarningCode = speech.UsedVoiceFallback ? "speech_voice_unavailable" : null,
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
        catch (Exception)
        {
            // Any other failure, including an unexpected cancellation whose source is not
            // the application token, is a local synthesis failure that degrades to text.
            _operationGate.Release();
            return SpokenOutputPreparation.SynthesisFailed;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_playbackSync)
            {
                _playbackCancellation?.Cancel();
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
            return await _play(_speech, cancellationToken);
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
        RequestedPreferences = effectivePreferences;
        State = new TerminalClientSpokenOutputState(
            SpokenOutputAvailability.Unavailable,
            effectivePreferences.IsMuted,
            NormalizeVoiceId(effectivePreferences.VoiceId),
            effectivePreferences.Rate,
            effectivePreferences.Volume);
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
        State = new TerminalClientSpokenOutputState(
            SpokenOutputAvailability.Unavailable,
            preferences.IsMuted,
            NormalizeVoiceId(preferences.VoiceId),
            preferences.Rate,
            preferences.Volume,
            WarningCode: null);
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

    private static string? NormalizeVoiceId(string? voiceId) => string.IsNullOrWhiteSpace(voiceId)
        ? null
        : TerminalTextSanitizer.NormalizeSingleLine(voiceId);
}
