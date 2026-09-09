using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class WindowsSpokenOutputTests
{
    [Fact]
    public async Task SensitiveMemoryStreamClearsItsAccessibleAudioBufferOnDispose()
    {
        var stream = new SensitiveMemoryStream();
        await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
        Assert.True(stream.TryGetBuffer(out var buffer));

        await stream.DisposeAsync();

        Assert.All(buffer.Array!.AsSpan(buffer.Offset, 4).ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void FactoryUsesUnavailableOutputOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var output = WindowsSpokenOutputFactory.Create(SpokenOutputPreferences.Default);

        Assert.Equal(SpokenOutputAvailability.Unavailable, output.State.Availability);
    }

    [Fact]
    public void SelectionOutsideWindowsNeverProbesTheSpeechSubsystem()
    {
        var probed = false;

        var output = WindowsSpokenOutputFactory.Select(
            SpokenOutputPreferences.Default,
            isWindows: false,
            hasEnabledVoices: () => { probed = true; return true; },
            createReadyCoordinator: () => throw new InvalidOperationException("must not build SAPI"));

        Assert.False(probed);
        Assert.Equal(SpokenOutputAvailability.Unavailable, output.State.Availability);
    }

    [Fact]
    public void SelectionWithoutEnabledVoicesIsUnavailableAndDoesNotBuildTheCoordinator()
    {
        var built = false;

        var output = WindowsSpokenOutputFactory.Select(
            SpokenOutputPreferences.Default,
            isWindows: true,
            hasEnabledVoices: () => false,
            createReadyCoordinator: () => { built = true; return new UnavailableSpokenOutputCoordinator(); });

        Assert.False(built);
        Assert.Equal(SpokenOutputAvailability.Unavailable, output.State.Availability);
    }

    [Fact]
    public void SelectionFallsBackToUnavailableWhenInitializationThrows()
    {
        var output = WindowsSpokenOutputFactory.Select(
            SpokenOutputPreferences.Default,
            isWindows: true,
            hasEnabledVoices: () => throw new InvalidOperationException("subsystem init failed"),
            createReadyCoordinator: () => throw new InvalidOperationException("unreachable"));

        Assert.Equal(SpokenOutputAvailability.Unavailable, output.State.Availability);
    }

    [Fact]
    public void SelectionWithEnabledVoicesReturnsTheReadyCoordinator()
    {
        var ready = new SpokenOutputCoordinator(
            new StubSynthesizer(),
            new StubPlayer(),
            SpokenOutputAvailability.Ready,
            SpokenOutputPreferences.Default);

        var output = WindowsSpokenOutputFactory.Select(
            SpokenOutputPreferences.Default,
            isWindows: true,
            hasEnabledVoices: () => true,
            createReadyCoordinator: () => ready);

        Assert.Same(ready, output);
        Assert.Equal(SpokenOutputAvailability.Ready, output.State.Availability);
    }

    [Fact]
    public void VoiceProjectionDropsDisabledVoicesAndOrdersOrdinally()
    {
        var projected = SpokenOutputVoiceProjection.EnabledVoices(
        [
            ("Zephyr", true),
            ("disabled voice", false),
            ("Aria", true),
            ("also disabled", false),
        ]);

        Assert.Equal(["Aria", "Zephyr"], projected.Select(voice => voice.Id));
    }

    [Fact]
    public void VoiceProjectionOfAnEmptyOrAllDisabledListIsEmpty()
    {
        Assert.Empty(SpokenOutputVoiceProjection.EnabledVoices([]));
        Assert.Empty(SpokenOutputVoiceProjection.EnabledVoices([("x", false), ("y", false)]));
    }

    [Fact]
    public void WaveDurationIsComputedFromTheHeader()
    {
        // 16 kHz, mono, 16-bit => 32000 bytes/sec. 48000 data bytes => 1.5 s.
        using var wav = BuildPcmWav(sampleRate: 16000, channels: 1, bitsPerSample: 16, dataBytes: 48000);

        Assert.Equal(TimeSpan.FromSeconds(1.5), WaveAudio.Duration(wav));
    }

    [Fact]
    public void WaveDurationSkipsUnknownChunksBeforeData()
    {
        using var wav = BuildPcmWav(22050, 1, 16, dataBytes: 44100, extraChunkBeforeData: true);

        Assert.Equal(TimeSpan.FromSeconds(1.0), WaveAudio.Duration(wav));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void WaveDurationOfANonWaveStreamIsZero(byte[] content)
    {
        using var stream = new MemoryStream(content);

        Assert.Equal(TimeSpan.Zero, WaveAudio.Duration(stream));
    }

    private static MemoryStream BuildPcmWav(
        int sampleRate,
        int channels,
        int bitsPerSample,
        int dataBytes,
        bool extraChunkBeforeData = false)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        var blockAlign = channels * (bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(0);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        if (extraChunkBeforeData)
        {
            writer.Write("LIST"u8.ToArray());
            writer.Write(6);
            writer.Write(new byte[6]);
        }

        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private sealed class StubSynthesizer : ISpeechSynthesizer
    {
        public Task<SynthesizedSpeech> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SynthesizedSpeech(new MemoryStream(), "audio/test"));
    }

    private sealed class StubPlayer : ISpeechPlayer
    {
        public Task PlayAsync(SynthesizedSpeech speech, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
