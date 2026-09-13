using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

/// <summary>
/// Exercises the segmented Kokoro pipeline (<c>SpokenTextSegmenter</c> plus the
/// coordinator's prefetch-while-playing loop) end to end through a real
/// <see cref="KokoroSpeechClient"/> and <see cref="KokoroSpeechSynthesizer"/>, so that a
/// stop reaching the in-flight prefetch synthesis is proven at the HTTP boundary rather
/// than assumed from the coordinator's internal wiring.
/// </summary>
public sealed class KokoroSegmentedPlaybackTests
{
    [Fact]
    public async Task StopDuringCurrentSegmentPlaybackCancelsThePendingPrefetch()
    {
        var handler = new SegmentAwareHandler(blockSecondCall: true);
        using var httpClient = new HttpClient(handler);
        var synthesizer = new KokoroSpeechSynthesizer(CreateClient(httpClient));
        var player = new BlockingPlayer();
        await using var coordinator = new SpokenOutputCoordinator(
            synthesizer,
            player,
            SpokenOutputAvailability.Ready,
            new SpokenOutputPreferences(requestedProvider: SpokenOutputProvider.Kokoro));

        var text = string.Concat(Enumerable.Repeat("Una frase con contenido suficiente. ", 12));
        var preparation = await coordinator.PrepareAsync(text, CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);

        await using (prepared)
        {
            var playback = prepared.PlayAsync(CancellationToken.None);
            await player.WaitForCallAsync();
            await handler.WaitForSecondCallStartedAsync();

            await coordinator.StopAsync(CancellationToken.None);

            var completed = await Task.WhenAny(playback, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(playback, completed);
            Assert.Equal(SpokenOutputPlaybackKind.Cancelled, (await playback).Kind);
        }

        var cancellationSignal = handler.SecondCallWasCancelledAsync();
        var raced = await Task.WhenAny(cancellationSignal, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(cancellationSignal, raced);
        Assert.True(await cancellationSignal);
    }

    [Fact]
    public async Task StopAfterPrefetchAlreadyCompletedStillReturnsPromptly()
    {
        var handler = new SegmentAwareHandler(blockSecondCall: false);
        using var httpClient = new HttpClient(handler);
        var synthesizer = new KokoroSpeechSynthesizer(CreateClient(httpClient));
        var player = new BlockingPlayer();
        await using var coordinator = new SpokenOutputCoordinator(
            synthesizer,
            player,
            SpokenOutputAvailability.Ready,
            new SpokenOutputPreferences(requestedProvider: SpokenOutputProvider.Kokoro));

        var text = string.Concat(Enumerable.Repeat("Una frase con contenido suficiente. ", 12));
        var preparation = await coordinator.PrepareAsync(text, CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);

        await using (prepared)
        {
            var playback = prepared.PlayAsync(CancellationToken.None);
            await player.WaitForCallAsync();

            // Give the unblocked prefetch time to finish and be sitting there completed
            // before Stop is requested, so the fix must observe and dispose it rather
            // than rely on cancellation to short-circuit it.
            await handler.WaitForSecondCallCompletedAsync();

            await coordinator.StopAsync(CancellationToken.None);

            var completed = await Task.WhenAny(playback, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(playback, completed);
            Assert.Equal(SpokenOutputPlaybackKind.Cancelled, (await playback).Kind);
        }

        Assert.Equal(2, handler.SpeechCallCount);
    }

    [Fact]
    public async Task PlaybackFailureCancelsThePendingPrefetchInsteadOfWaitingForItsTimeout()
    {
        var handler = new SegmentAwareHandler(blockSecondCall: true);
        using var httpClient = new HttpClient(handler);
        var synthesizer = new KokoroSpeechSynthesizer(CreateClient(httpClient));
        var player = new FailingPlayer();
        await using var coordinator = new SpokenOutputCoordinator(
            synthesizer,
            player,
            SpokenOutputAvailability.Ready,
            new SpokenOutputPreferences(requestedProvider: SpokenOutputProvider.Kokoro));

        var text = string.Concat(Enumerable.Repeat("Una frase con contenido suficiente. ", 12));
        var preparation = await coordinator.PrepareAsync(text, CancellationToken.None);
        var prepared = Assert.IsAssignableFrom<IPreparedSpokenOutput>(preparation.PreparedOutput);

        await using (prepared)
        {
            var playback = prepared.PlayAsync(CancellationToken.None);
            await player.WaitForCallAsync();
            await handler.WaitForSecondCallStartedAsync();

            player.ReleaseFailure();

            // Bounded well under the client's 18-second synthesis timeout: a genuine
            // playback failure must cancel the pending prefetch and report promptly,
            // not wait behind a synthesis nobody will ever play.
            var completed = await Task.WhenAny(playback, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(playback, completed);
            Assert.Equal(SpokenOutputPlaybackKind.PlaybackFailed, (await playback).Kind);
        }

        var cancellationSignal = handler.SecondCallWasCancelledAsync();
        var raced = await Task.WhenAny(cancellationSignal, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(cancellationSignal, raced);
        Assert.True(await cancellationSignal);
    }

    private static KokoroSpeechClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        static () => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        KokoroServiceOptions.Create(new Uri("http://127.0.0.1:57321/")));

    private static byte[] CreateWave(int dataLength = 480)
    {
        var wave = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), (uint)(wave.Length - 8));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), 24000);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), (uint)dataLength);
        return wave;
    }

    private sealed class BlockingPlayer : ISpeechPlayer
    {
        private readonly TaskCompletionSource _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PlayAsync(SynthesizedSpeech speech, CancellationToken cancellationToken)
        {
            _called.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitForCallAsync() => _called.Task;
    }

    private sealed class FailingPlayer : ISpeechPlayer
    {
        private readonly TaskCompletionSource _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PlayAsync(SynthesizedSpeech speech, CancellationToken cancellationToken)
        {
            _called.TrySetResult();
            await _releaseFailure.Task;
            throw new InvalidOperationException("Synthetic playback failure.");
        }

        public Task WaitForCallAsync() => _called.Task;

        public void ReleaseFailure() => _releaseFailure.TrySetResult();
    }

    /// <summary>
    /// Fakes the Kokoro HTTP boundary: the first <c>/v1/speech</c> call always succeeds
    /// immediately; the second either blocks until its own request is cancelled
    /// (simulating a prefetch caught mid-flight by /stop) or succeeds immediately
    /// (simulating a prefetch that finished before /stop arrived).
    /// </summary>
    private sealed class SegmentAwareHandler(bool blockSecondCall) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _secondCallStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondCallCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondCallCancelled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _speechCallCount;

        public int SpeechCallCount => _speechCallCount;

        public Task WaitForSecondCallStartedAsync() => _secondCallStarted.Task;

        public Task WaitForSecondCallCompletedAsync() => _secondCallCompleted.Task;

        public Task<bool> SecondCallWasCancelledAsync() => _secondCallCancelled.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/v1/speech")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var callNumber = Interlocked.Increment(ref _speechCallCount);
            if (callNumber != 2)
            {
                return BuildWavResponse();
            }

            _secondCallStarted.TrySetResult();
            if (!blockSecondCall)
            {
                _secondCallCompleted.TrySetResult();
                return BuildWavResponse();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return BuildWavResponse();
            }
            catch (OperationCanceledException)
            {
                _secondCallCancelled.TrySetResult(true);
                throw;
            }
        }

        private static HttpResponseMessage BuildWavResponse()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateWave()),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            return response;
        }
    }
}
