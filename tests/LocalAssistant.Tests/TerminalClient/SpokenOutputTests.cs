using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class SpokenOutputTests
{
    [Fact]
    public void ProviderSpecificPreferenceUpdatesPreserveTheOtherProvider()
    {
        var preferences = new SpokenOutputPreferences(
            voiceId: "Sapi voice",
            rate: 3,
            volume: 40,
            requestedProvider: SpokenOutputProvider.Kokoro,
            kokoroProfileId: "jarvis-es-alt",
            kokoroLanguage: "es",
            kokoroVolume: 65);

        var kokoroVolume = preferences.WithKokoroVolume(20);
        var sapiRate = kokoroVolume.WithSapiRate(-2);

        Assert.Equal("jarvis-es-alt", sapiRate.KokoroProfileId);
        Assert.Equal(20, sapiRate.KokoroVolume);
        Assert.Equal("Sapi voice", sapiRate.VoiceId);
        Assert.Equal(-2, sapiRate.Rate);
        Assert.Equal(SpokenOutputProvider.Kokoro, sapiRate.RequestedProvider);
    }

    [Fact]
    public async Task SnapshotProjectsKokoroPreferencesNotSapiFieldsWhenKokoroIsSelected()
    {
        var preferences = new SpokenOutputPreferences(
            voiceId: "Sapi voice",
            rate: 3,
            volume: 40,
            requestedProvider: SpokenOutputProvider.Kokoro,
            kokoroProfileId: "jarvis-es-alt",
            kokoroLanguage: "es",
            kokoroVolume: 65);
        await using var coordinator = CreateCoordinator(new RecordingSynthesizer(), new RecordingPlayer(), preferences);

        Assert.Equal("jarvis-es-alt", coordinator.State.VoiceId);
        Assert.Equal(65, coordinator.State.Volume);
        // Kokoro synthesizes at a fixed rate; there is no SAPI-style scale to show.
        Assert.Equal(0, coordinator.State.Rate);
    }

    [Fact]
    public async Task ChangingKokoroVolumeUpdatesTheDisplayedVolumeInsteadOfTheStaleSapiValue()
    {
        var preferences = new SpokenOutputPreferences(
            voiceId: "Sapi voice",
            volume: 40,
            requestedProvider: SpokenOutputProvider.Kokoro,
            kokoroVolume: 65);
        await using var coordinator = CreateCoordinator(new RecordingSynthesizer(), new RecordingPlayer(), preferences);

        coordinator.UpdatePreferences(preferences.WithKokoroVolume(30));

        Assert.Equal(30, coordinator.State.Volume);
    }

    [Fact]
    public void RateDisplayShowsFixedForKokoroInsteadOfAMisleadingSapiStyleNumber()
    {
        static string RateDisplay(
            SpokenOutputProvider requestedProvider,
            SpokenOutputProvider? effectiveProvider) => new TerminalClientSpokenOutputState(
                SpokenOutputAvailability.Ready,
                IsMuted: false,
                Rate: 0,
                RequestedProvider: requestedProvider,
                EffectiveProvider: effectiveProvider).RateDisplay;

        // Before any synthesis result, the requested provider governs.
        Assert.Equal("fixed (1.0)", RateDisplay(SpokenOutputProvider.Kokoro, effectiveProvider: null));
        Assert.Equal("0", RateDisplay(SpokenOutputProvider.Sapi, effectiveProvider: null));
        // Once a result is known, the effective provider governs, even if it differs
        // from what was requested (a fallback in either direction).
        Assert.Equal("0", RateDisplay(SpokenOutputProvider.Kokoro, SpokenOutputProvider.Sapi));
        Assert.Equal("fixed (1.0)", RateDisplay(SpokenOutputProvider.Sapi, SpokenOutputProvider.Kokoro));
    }

    [Fact]
    public async Task NoneProviderProjectsNeutralDisplayValuesInsteadOfAStaleProvidersSettings()
    {
        var preferences = new SpokenOutputPreferences(
            voiceId: "Sapi voice",
            volume: 40,
            requestedProvider: SpokenOutputProvider.None,
            kokoroVolume: 65);
        await using var coordinator = CreateCoordinator(new RecordingSynthesizer(), new RecordingPlayer(), preferences);

        Assert.Null(coordinator.State.VoiceId);
        Assert.Equal(0, coordinator.State.Rate);
        Assert.Equal(SpokenOutputPreferences.Default.Volume, coordinator.State.Volume);
    }

    [Theory]
    [InlineData("Hola 😀 qué tal", "Hola qué tal")]
    [InlineData("Listo ✅ y hecho", "Listo y hecho")]
    [InlineData("Atención ⚠️ ahora", "Atención ahora")]
    [InlineData("familia 👨‍👩‍👧‍👦 completa", "familia completa")]
    [InlineData("banderas 🇪🇸🇬🇧 varias", "banderas varias")]
    [InlineData("estrella ⭐ y flecha ⬆️", "estrella y flecha")]
    [InlineData("20°C cuesta 5€ (±2)", "20°C cuesta 5€ (±2)")]
    [InlineData("línea uno 🙂\nlínea dos", "línea uno\nlínea dos")]
    [InlineData("   texto   con   espacios   ", "texto con espacios")]
    public void ForSpeechRemovesUnspeakableSymbolsButKeepsReadableText(string input, string expected)
    {
        Assert.Equal(expected, SpokenText.ForSpeech(input));
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("👍🎉✨")]
    [InlineData("   🙂   ")]
    public void ForSpeechReturnsEmptyWhenNothingIsSpeakable(string input)
    {
        Assert.Equal(string.Empty, SpokenText.ForSpeech(input));
    }

    [Fact]
    public void ForSpeechDropsControlCharactersButKeepsNewlines()
    {
        Assert.Equal("uno\ndos", SpokenText.ForSpeech("uno\u0007 \ndos "));
    }

    [Fact]
    public void PreferencesUseApprovedDefaultsAndRejectInvalidBounds()
    {
        var preferences = SpokenOutputPreferences.Default;

        Assert.Null(preferences.VoiceId);
        Assert.Equal(0, preferences.Rate);
        Assert.Equal(100, preferences.Volume);
        Assert.False(preferences.IsMuted);
        _ = new SpokenOutputPreferences("Voice", -10, 0, false);
        _ = new SpokenOutputPreferences("Voice", 10, 100, true);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpokenOutputPreferences("Voice", -11, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpokenOutputPreferences("Voice", 0, 101));
        Assert.Throws<ArgumentException>(() => new SpokenOutputPreferences(" ", 0, 100));
    }

    [Fact]
    public async Task LocalStopCancelsActivePlaybackWithoutCancellingTheCaller()
    {
        var player = new RecordingPlayer(blockUntilCancellation: true);
        await using var coordinator = CreateCoordinator(new RecordingSynthesizer(), player);
        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);
        await using (prepared)
        {
            var playback = prepared.PlayAsync(CancellationToken.None);
            await player.WaitForCallAsync();

            await coordinator.StopAsync(CancellationToken.None);

            Assert.Equal(SpokenOutputPlaybackKind.Cancelled, (await playback).Kind);
        }
    }

    [Fact]
    public async Task UpdatingPreferencesChangesTheNextSynthesisRequest()
    {
        var synthesizer = new RecordingSynthesizer();
        await using var coordinator = CreateCoordinator(synthesizer, new RecordingPlayer());
        var preferences = new SpokenOutputPreferences("Voice A", 3, 45, false);

        coordinator.UpdatePreferences(preferences);
        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        await using var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);

        Assert.Equal(preferences, synthesizer.Requests.Single().Preferences);
        Assert.Equal("Voice A", coordinator.State.VoiceId);
        Assert.Equal(3, coordinator.State.Rate);
        Assert.Equal(45, coordinator.State.Volume);
    }

    [Fact]
    public async Task VoiceFallbackKeepsTheRequestedPreferenceAndPublishesOnlySafeState()
    {
        var requested = new SpokenOutputPreferences("Removed voice", 2, 70, false);
        await using var coordinator = CreateCoordinator(
            new RecordingSynthesizer(effectiveVoiceId: "Windows default", usedVoiceFallback: true),
            new RecordingPlayer(),
            requested);

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        await using var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);

        Assert.Equal(requested, coordinator.RequestedPreferences);
        Assert.Equal("Windows default", coordinator.State.VoiceId);
        Assert.Equal("speech_voice_unavailable", coordinator.State.WarningCode);
        // SAPI substituting a voice it could find is not a provider fallback: the
        // provider never changed away from the one requested.
        Assert.False(coordinator.State.UsedProviderFallback);
    }

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
            new SpokenOutputPreferences(isMuted: true));

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.Muted, preparation.Kind);
        Assert.Equal(0, synthesizer.CallCount);
        Assert.Equal(0, player.CallCount);
    }

    [Fact]
    public async Task NoneProviderDoesNotInvokeSynthesisOrPlaybackAndNeverFails()
    {
        var synthesizer = new RecordingSynthesizer();
        var player = new RecordingPlayer();
        await using var coordinator = CreateCoordinator(
            synthesizer,
            player,
            new SpokenOutputPreferences(requestedProvider: SpokenOutputProvider.None));

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.Muted, preparation.Kind);
        Assert.Equal(0, synthesizer.CallCount);
        Assert.Equal(0, player.CallCount);
    }

    [Fact]
    public async Task KokoroNotConfiguredWithFallbackDisabledDegradesToTextInsteadOfSilentlyUsingSapi()
    {
        var sapi = new RecordingSynthesizer();
        var selector = new ProviderSelectingSpeechSynthesizer(sapi, kokoro: null);
        await using var coordinator = CreateCoordinator(
            selector,
            new RecordingPlayer(),
            new SpokenOutputPreferences(
                requestedProvider: SpokenOutputProvider.Kokoro,
                useSapiFallback: false));

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.SynthesisFailed, preparation.Kind);
        Assert.Equal(KokoroClientFailureKind.NotConfigured, preparation.KokoroFailure);
        Assert.Equal(0, sapi.CallCount);
    }

    [Fact]
    public async Task KokoroNotConfiguredWithFallbackEnabledStillUsesSapi()
    {
        var sapi = new RecordingSynthesizer();
        var selector = new ProviderSelectingSpeechSynthesizer(sapi, kokoro: null);
        await using var coordinator = CreateCoordinator(
            selector,
            new RecordingPlayer(),
            new SpokenOutputPreferences(
                requestedProvider: SpokenOutputProvider.Kokoro,
                useSapiFallback: true));

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.Prepared, preparation.Kind);
        Assert.Equal(1, sapi.CallCount);
        // The provider genuinely changed (Kokoro requested, SAPI effective), unlike a
        // mere SAPI voice substitution; this must read as a provider fallback.
        Assert.True(coordinator.State.UsedProviderFallback);
        Assert.Equal(SpokenOutputProvider.Sapi, coordinator.State.EffectiveProvider);
        await preparation.PreparedOutput!.DisposeAsync();
    }

    [Fact]
    public async Task FallbackToSapiReprojectsRateAndVolumeInsteadOfLeavingKokorosValues()
    {
        var sapi = new RecordingSynthesizer(effectiveVoiceId: "Microsoft Helena");
        var selector = new ProviderSelectingSpeechSynthesizer(sapi, kokoro: null);
        var preferences = new SpokenOutputPreferences(
            voiceId: "Sapi voice",
            rate: 4,
            volume: 55,
            requestedProvider: SpokenOutputProvider.Kokoro,
            kokoroProfileId: "jarvis-es-alt",
            kokoroVolume: 90,
            useSapiFallback: true);
        await using var coordinator = CreateCoordinator(selector, new RecordingPlayer(), preferences);

        // Before synthesis, the display must reflect the requested provider (Kokoro).
        Assert.Equal("jarvis-es-alt", coordinator.State.VoiceId);
        Assert.Equal(0, coordinator.State.Rate);
        Assert.Equal(90, coordinator.State.Volume);

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);

        Assert.Equal(SpokenOutputPreparationKind.Prepared, preparation.Kind);
        Assert.Equal(SpokenOutputProvider.Sapi, coordinator.State.EffectiveProvider);
        // After the fallback, the display must reflect SAPI (the effective provider),
        // not the requested Kokoro settings SAPI is not actually using.
        Assert.Equal("Microsoft Helena", coordinator.State.VoiceId);
        Assert.Equal(4, coordinator.State.Rate);
        Assert.Equal(55, coordinator.State.Volume);
        await preparation.PreparedOutput!.DisposeAsync();
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

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("localstop")]
    [InlineData("globalcancel")]
    public async Task TheSensitiveAudioBufferIsClearedAfterEveryPlaybackOutcome(string outcome)
    {
        var stream = new SensitiveMemoryStream();
        await stream.WriteAsync(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7 });
        Assert.True(stream.TryGetBuffer(out var buffer));
        var block = outcome is "localstop" or "globalcancel";
        var synthesizer = new RecordingSynthesizer(() => stream);
        var player = new RecordingPlayer(
            exception: outcome == "failure" ? new InvalidOperationException("Failure.") : null,
            blockUntilCancellation: block);
        await using var coordinator = CreateCoordinator(synthesizer, player);
        using var cancellation = new CancellationTokenSource();

        var preparation = await coordinator.PrepareAsync("Final response.", CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);
        await using (prepared)
        {
            var playbackToken = outcome == "globalcancel" ? cancellation.Token : CancellationToken.None;
            var playback = prepared.PlayAsync(playbackToken);
            if (block)
            {
                await player.WaitForCallAsync();
                if (outcome == "localstop")
                {
                    await coordinator.StopAsync(CancellationToken.None);
                }
                else
                {
                    cancellation.Cancel();
                }
            }

            if (outcome == "globalcancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
            }
            else
            {
                await playback;
            }
        }

        Assert.All(buffer.Array!.AsSpan(buffer.Offset, 8).ToArray(), value => Assert.Equal((byte)0, value));
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
    public async Task AFailingVoiceEnumerationDegradesToNoVoicesInsteadOfThrowing()
    {
        var catalog = new ThrowingVoiceCatalog();
        await using var coordinator = CreateCoordinator(catalog, new RecordingPlayer());

        var voices = await coordinator.GetVoicesAsync(CancellationToken.None);

        Assert.Empty(voices);
    }

    [Fact]
    public async Task VoiceEnumerationStillPropagatesCallerCancellation()
    {
        var catalog = new ThrowingVoiceCatalog();
        await using var coordinator = CreateCoordinator(catalog, new RecordingPlayer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.GetVoicesAsync(cancellation.Token));
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
        private readonly Func<MemoryStream> _streamFactory;
        private readonly Exception? _exception;
        private readonly bool _blockUntilCancellation;
        private readonly string? _effectiveVoiceId;
        private readonly bool _usedVoiceFallback;
        private readonly TaskCompletionSource<bool> _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingSynthesizer(
            Func<MemoryStream>? streamFactory = null,
            Exception? exception = null,
            bool blockUntilCancellation = false,
            string? effectiveVoiceId = null,
            bool usedVoiceFallback = false)
        {
            _streamFactory = streamFactory ?? (() => new TrackingStream());
            _exception = exception;
            _blockUntilCancellation = blockUntilCancellation;
            _effectiveVoiceId = effectiveVoiceId;
            _usedVoiceFallback = usedVoiceFallback;
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

            return new SynthesizedSpeech(
                _streamFactory(),
                "audio/test",
                _effectiveVoiceId,
                _usedVoiceFallback);
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

    private sealed class ThrowingVoiceCatalog : ISpeechSynthesizer, ISpeechVoiceCatalog
    {
        public Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The speech subsystem failed mid-session.");
        }

        public Task<SynthesizedSpeech> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SynthesizedSpeech(new MemoryStream(), "audio/test"));
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
