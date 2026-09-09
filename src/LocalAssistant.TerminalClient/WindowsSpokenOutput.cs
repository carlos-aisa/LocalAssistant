using System.Buffers.Binary;
using System.Media;
using System.Runtime.Versioning;
using System.Speech.Synthesis;

namespace LocalAssistant.TerminalClient;

internal static class WindowsSpokenOutputFactory
{
    public static ISpokenOutputCoordinator Create(SpokenOutputPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return OperatingSystem.IsWindows()
            ? CreateForWindows(preferences)
            : new UnavailableSpokenOutputCoordinator(preferences);
    }

    /// <summary>
    /// Platform-neutral selection logic, split from the SAPI construction so tests can
    /// drive the no-voices and initialization-failure paths without a real synthesizer.
    /// </summary>
    internal static ISpokenOutputCoordinator Select(
        SpokenOutputPreferences preferences,
        bool isWindows,
        Func<bool> hasEnabledVoices,
        Func<ISpokenOutputCoordinator> createReadyCoordinator)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(hasEnabledVoices);
        ArgumentNullException.ThrowIfNull(createReadyCoordinator);
        if (!isWindows)
        {
            return new UnavailableSpokenOutputCoordinator(preferences);
        }

        try
        {
            return hasEnabledVoices()
                ? createReadyCoordinator()
                : new UnavailableSpokenOutputCoordinator(preferences);
        }
        catch (Exception)
        {
            return new UnavailableSpokenOutputCoordinator(preferences);
        }
    }

    [SupportedOSPlatform("windows")]
    private static ISpokenOutputCoordinator CreateForWindows(SpokenOutputPreferences preferences) =>
        Select(
            preferences,
            isWindows: true,
            WindowsSpeechSynthesizer.HasEnabledVoices,
            () => new SpokenOutputCoordinator(
                new WindowsSpeechSynthesizer(),
                new WindowsSpeechPlayer(),
                SpokenOutputAvailability.Ready,
                preferences));
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeechSynthesizer : ISpeechSynthesizer, ISpeechVoiceCatalog
{
    public Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetVoices());
    }

    public static bool HasEnabledVoices() => GetVoices().Count > 0;

    private static IReadOnlyList<SpokenOutputVoice> GetVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
#pragma warning disable CA1304 // The client must expose every enabled installed voice, not only the current UI culture.
        var installed = synthesizer.GetInstalledVoices();
#pragma warning restore CA1304
        return SpokenOutputVoiceProjection.EnabledVoices(
            installed.Select(voice => (voice.VoiceInfo.Name, voice.Enabled)));
    }

    public async Task<SynthesizedSpeech> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = new SensitiveMemoryStream();
        string? effectiveVoiceId = null;
        var usedVoiceFallback = false;
        try
        {
            await Task.Run(async () =>
            {
                using var synthesizer = new SpeechSynthesizer();
                usedVoiceFallback = Configure(synthesizer, request.Preferences);
                effectiveVoiceId = synthesizer.Voice.Name;

                // Must be SetOutputToWaveStream (a full RIFF/WAVE container), not
                // SetOutputToAudioStream (headerless PCM): SoundPlayer only plays WAV.
                synthesizer.SetOutputToWaveStream(stream);
                var completion = new TaskCompletionSource<SpeakCompletedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<SpeakCompletedEventArgs>? handler = null;
                handler = (_, args) => completion.TrySetResult(args);
                synthesizer.SpeakCompleted += handler;
                try
                {
                    using var registration = cancellationToken.Register(synthesizer.SpeakAsyncCancelAll);
                    synthesizer.SpeakAsync(request.Text);
                    var result = await completion.Task.WaitAsync(cancellationToken);
                    if (result.Error is not null)
                    {
                        throw result.Error;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Cancelled)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
                finally
                {
                    synthesizer.SpeakCompleted -= handler;
                }
            }, cancellationToken);
            stream.Position = 0;
            return new SynthesizedSpeech(
                stream,
                "audio/wav",
                effectiveVoiceId,
                usedVoiceFallback);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static bool Configure(SpeechSynthesizer synthesizer, SpokenOutputPreferences preferences)
    {
        var usedVoiceFallback = false;
        if (preferences.VoiceId is not null)
        {
            try
            {
                synthesizer.SelectVoice(preferences.VoiceId);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                // The selected voice can be uninstalled or disabled after preferences
                // were persisted (ArgumentException when missing, InvalidOperationException
                // when present but not loadable). SAPI's configured default is the safe
                // local fallback in both cases.
                usedVoiceFallback = true;
            }
        }

        synthesizer.Rate = preferences.Rate;
        synthesizer.Volume = preferences.Volume;
        return usedVoiceFallback;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeechPlayer : ISpeechPlayer
{
    public async Task PlayAsync(SynthesizedSpeech speech, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(speech);
        cancellationToken.ThrowIfCancellationRequested();
        speech.Content.Position = 0;

        using var player = new SoundPlayer(speech.Content);
        await Task.Run(player.Load, CancellationToken.None);

        // Play asynchronously and drive completion from the WAV duration, so /stop can
        // interrupt from any thread via SoundPlayer.Stop(); SND_SYNC playback cannot be
        // stopped reliably from a different thread.
        speech.Content.Position = 0;
        var duration = WaveAudio.Duration(speech.Content);
        void Stop()
        {
            try
            {
                player.Stop();
            }
            catch (Exception)
            {
                // Stop() must never break the cancellation pipeline.
            }
        }

        await using var registration = cancellationToken.Register(Stop);
        cancellationToken.ThrowIfCancellationRequested();

        if (duration <= TimeSpan.Zero)
        {
            // Header could not be read: fall back to synchronous playback. Stop() may not
            // interrupt this on every machine, but the token still ends the wait.
            await Task.Run(player.PlaySync, CancellationToken.None).WaitAsync(cancellationToken);
            return;
        }

        player.Play();
        try
        {
            await Task.Delay(duration + TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        finally
        {
            Stop();
        }
    }
}

/// <summary>
/// Reads the playing time from a RIFF/WAVE header. Platform-neutral and split out so the
/// parsing (a real failure surface) is covered without audio hardware.
/// </summary>
internal static class WaveAudio
{
    public static TimeSpan Duration(Stream wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        try
        {
            Span<byte> riff = stackalloc byte[12];
            if (wave.Read(riff) != 12 ||
                !riff[..4].SequenceEqual("RIFF"u8) ||
                !riff[8..12].SequenceEqual("WAVE"u8))
            {
                return TimeSpan.Zero;
            }

            var sampleRate = 0;
            var channels = 0;
            var bitsPerSample = 0;
            long dataBytes = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (wave.Read(chunk) == 8)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (wave.Read(fmt) != 16)
                    {
                        return TimeSpan.Zero;
                    }

                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
                    sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..8]);
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);
                    if (size > 16)
                    {
                        wave.Seek(size - 16, SeekOrigin.Current);
                    }
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    dataBytes = size;
                    break;
                }
                else
                {
                    wave.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }

            var byteRate = (long)sampleRate * channels * (bitsPerSample / 8);
            return byteRate > 0
                ? TimeSpan.FromSeconds((double)dataBytes / byteRate)
                : TimeSpan.Zero;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }
}

/// <summary>
/// Pure projection of the installed-voice list: drops disabled voices and returns a
/// deterministic ordinal ordering. Split out (and platform-neutral) so it is covered
/// without a real synthesizer.
/// </summary>
internal static class SpokenOutputVoiceProjection
{
    public static IReadOnlyList<SpokenOutputVoice> EnabledVoices(
        IEnumerable<(string Name, bool Enabled)> installed) =>
        installed
            .Where(voice => voice.Enabled)
            .Select(voice => new SpokenOutputVoice(voice.Name))
            .OrderBy(voice => voice.Id, StringComparer.Ordinal)
            .ToArray();
}

internal sealed class SensitiveMemoryStream : MemoryStream
{
    protected override void Dispose(bool disposing)
    {
        if (disposing && TryGetBuffer(out var buffer))
        {
            Array.Clear(buffer.Array!, buffer.Offset, checked((int)Length));
        }

        base.Dispose(disposing);
    }
}
