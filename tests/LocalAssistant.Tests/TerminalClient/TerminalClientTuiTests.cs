using System.Collections.Concurrent;
using System.Globalization;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientTuiTests
{
    [Fact]
    public async Task SecretInputIsReturnedWithoutEnteringTheTranscript()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var request = new TerminalInputRequest(TerminalInputKind.Secret, "Credential: ");
        var readTask = Task.Run(() => console.ReadSecret(request));

        await console.WaitForInputAsync();
        console.CompleteInput("secret-value");

        Assert.Equal("secret-value", await readTask);
        Assert.Empty(console.DrainTranscript());
        Assert.False(console.TryGetInputRequest(out _));
    }

    [Fact]
    public void StateSinkCoalescesOrdinarySnapshotsAndPreservesPriorityTransitions()
    {
        var sink = new TerminalClientTuiStateSink();
        var ready = TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
        };
        var error = ready with
        {
            Error = new TerminalClientOperationError(
                TerminalClientErrorSeverity.Recoverable,
                false,
                "network_error",
                "The operation failed.",
                "turn"),
        };

        sink.OnStateChanged(ready);
        sink.OnStateChanged(error);
        sink.OnStateChanged(ready);

        var priority = sink.DrainPriority().ToList();

        Assert.Contains(priority, snapshot => snapshot.Error?.Code == "network_error");
        Assert.Equal(ready, sink.TakeLatest());
    }

    [Fact]
    public async Task CancellationUnblocksPendingInputAndRestoresTheTerminal()
    {
        using var cancellationSource = new CancellationTokenSource();
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            var input = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return input is null ? 2 : 0;
        }, cancellationSource.Token);

        await console.WaitForInputAsync();
        cancellationSource.Cancel();

        Assert.Equal(2, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(driver.Restored);
    }

    [Fact]
    public async Task ResizeAndPrioritySnapshotsProduceSeparateFramesWithExpiry()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var sink = new TerminalClientTuiStateSink();
        var driver = new FakeTerminalDriver(new TerminalSize(40, 12));
        var host = new TerminalClientTuiHost(console, sink, driver);
        using var cancellationSource = new CancellationTokenSource();
        var expiry = DateTimeOffset.Parse("2026-09-06T12:00:00+00:00", CultureInfo.InvariantCulture);

        var runTask = host.RunAsync(async _ =>
        {
            var input = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return input is null ? 2 : 0;
        }, cancellationSource.Token);

        sink.OnStateChanged(TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
            Activity = TerminalClientActivity.AwaitingConfirmation,
            PendingConfirmation = new TerminalClientPendingConfirmation("create_reminder", expiry),
        });
        await console.WaitForInputAsync();
        await WaitForFramesAsync(driver, 2);
        driver.Size = new TerminalSize(30, 10);
        await WaitForFramesAsync(driver, 3);

        sink.OnStateChanged(TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
            Error = new TerminalClientOperationError(
                TerminalClientErrorSeverity.Recoverable,
                false,
                "temporary_failure",
                "A temporary failure occurred.",
                "send"),
        });
        sink.OnStateChanged(TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
        });
        await WaitForFramesAsync(driver, 5);
        console.CancelInput();
        await runTask;

        Assert.Contains(driver.Frames, frame => frame.Any(line => line.Contains("expires 2026-09-06 12:00 UTC", StringComparison.Ordinal)));
        Assert.Contains(driver.Frames, frame => frame.Any(line => line.Contains("temporary_failure", StringComparison.Ordinal)));
        Assert.Contains(driver.Frames, frame => frame.All(line => line.Length <= 30));
    }

    [Fact]
    public async Task TranscriptIsClippedAndScrollKeysOnlyChangeTheViewport()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(40, 10));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            var input = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return input is null ? 2 : 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        for (var index = 0; index < 10; index++)
        {
            console.WriteConversationMessage("Assistant", $"item-{index:D2}");
        }

        await WaitForFramesAsync(driver, 2);
        driver.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
        await WaitForFramesAsync(driver, 3);
        console.CancelInput();
        await runTask;

        Assert.All(driver.Frames.SelectMany(frame => frame), line => Assert.True(line.Length <= 40));
        Assert.Contains(driver.Frames, frame => frame.Any(line => line.Contains("item-09", StringComparison.Ordinal)));
        Assert.Contains(driver.Frames, frame => frame.Any(line => line.Contains("item-00", StringComparison.Ordinal)));
        Assert.True(driver.InputReadCount > 0);
    }

    [Fact]
    public async Task EndOfInputUnblocksPendingInputAndRestoresTheTerminal()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            var input = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return input is null ? 2 : 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueEndOfInput();

        Assert.Equal(2, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(driver.Restored);
    }

    [Fact]
    public async Task ClosingInputChannelUnblocksEverySubsequentRead()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var firstRead = Task.Run(() => console.ReadLine(new TerminalInputRequest(
            TerminalInputKind.Line,
            "Pairing code: ")));

        await console.WaitForInputAsync();
        console.CloseInput();

        Assert.Null(await firstRead);
        Assert.Null(console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "Client name: ")));
        Assert.Equal(string.Empty, console.ReadSecret(new TerminalInputRequest(TerminalInputKind.Secret, "Secret: ")));
        Assert.False(console.TryGetInputRequest(out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationDuringAnyPairingPromptDoesNotPublishAnotherPrompt(int promptIndex)
    {
        using var cancellationSource = new CancellationTokenSource();
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var runTask = host.RunAsync(
            cancellationToken => RunPairingInputSequenceAsync(console, cancellationToken),
            cancellationSource.Token);

        await console.WaitForInputAsync();
        if (promptIndex > 0)
        {
            console.CompleteInput(string.Empty);
            await console.WaitForInputAsync();
        }

        if (promptIndex > 1)
        {
            console.CompleteInput("pairing-challenge");
            await console.WaitForInputAsync();
        }

        cancellationSource.Cancel();

        Assert.Equal(2, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(console.TryGetInputRequest(out _));
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public async Task CancellationBetweenPairingPromptsClosesTheNextReadImmediately()
    {
        using var cancellationSource = new CancellationTokenSource();
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var runTask = host.RunAsync(
            cancellationToken => RunPairingInputSequenceAsync(console, cancellationToken),
            cancellationSource.Token);

        await console.WaitForInputAsync();
        console.CompleteInput(string.Empty);
        cancellationSource.Cancel();

        Assert.Equal(2, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(console.TryGetInputRequest(out _));
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public async Task EscapeCompletesOnlyTheCurrentPromptAndAllowsTheNextPrompt()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var runTask = host.RunAsync(async _ =>
        {
            var first = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "First: ")));
            var second = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Second: ")));
            return first == string.Empty && second == "next" ? 0 : 1;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        await WaitForInputRequestAsync(console, "Second: ");
        driver.EnqueueCharacters("next");
        driver.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public async Task HostRestoresTheTerminalExactlyOnceWhenTheOperationThrows()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(
            _ => Task.FromException<int>(new InvalidOperationException("expected")),
            CancellationToken.None));

        Assert.Equal(1, driver.RestoreCount);
    }

    [Theory]
    [InlineData(ConsoleKey.D)]
    [InlineData(ConsoleKey.Z)]
    public async Task ControlEndOfInputWithEmptyBufferClosesTheInputChannel(ConsoleKey key)
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var runTask = host.RunAsync(async _ =>
        {
            var input = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return input is null ? 2 : 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.Enqueue(new ConsoleKeyInfo('\0', key, false, false, true));

        Assert.Equal(2, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(driver.Restored);
    }

    [Theory]
    [InlineData(ConsoleKey.D)]
    [InlineData(ConsoleKey.Z)]
    public async Task ControlEndOfInputWithTextDoesNotCompleteTheInput(ConsoleKey key)
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        string? received = null;
        var runTask = host.RunAsync(async _ =>
        {
            received = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("hola");
        driver.Enqueue(new ConsoleKeyInfo('\0', key, false, false, true));
        driver.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("hola", received);
    }

    [Fact]
    public async Task CancellationClearsMaskedSecretBeforeRestoringTheTerminal()
    {
        using var cancellationSource = new CancellationTokenSource();
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadSecret(new TerminalInputRequest(
                TerminalInputKind.Secret,
                "Challenge: ")));
            return 2;
        }, cancellationSource.Token);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("private-value");
        await WaitForFramesAsync(driver, 2);
        cancellationSource.Cancel();

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(driver.Frames.Last(), line => line.Contains('*'));
        Assert.DoesNotContain(driver.Frames.SelectMany(frame => frame), line => line.Contains("private-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MinimumViewportKeepsInputConfirmationErrorAndStateVisible()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var sink = new TerminalClientTuiStateSink();
        var driver = new FakeTerminalDriver(new TerminalSize(40, 8));
        var host = new TerminalClientTuiHost(console, sink, driver);
        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "You: ")));
            return 0;
        }, CancellationToken.None);

        sink.OnStateChanged(TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Activity = TerminalClientActivity.AwaitingConfirmation,
            Provider = "fake",
            PendingConfirmation = new TerminalClientPendingConfirmation(
                "tool\n\u001B]8;unsafe\u0007",
                DateTimeOffset.Parse("2026-09-06T12:00:00+00:00", CultureInfo.InvariantCulture)),
            Error = new TerminalClientOperationError(
                TerminalClientErrorSeverity.Recoverable,
                true,
                "network\nerror",
                "failed\u001B[2J",
                "turn"),
        });

        await console.WaitForInputAsync();
        await WaitForFramesAsync(driver, 2);
        console.CancelInput();
        await runTask;

        var frame = driver.Frames.Last();
        Assert.Contains(frame, line => line.StartsWith("You:", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("expires 2026-09-06 12:00 UTC", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("UNCERTAIN", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.StartsWith("State:", StringComparison.Ordinal));
        Assert.All(frame, line => Assert.DoesNotContain('\n', line));
        Assert.All(frame, line => Assert.DoesNotContain('\u001B', line));
    }

    [Fact]
    public void TranscriptKeepsTheRecentTailWithinItsCharacterAndLineBudgets()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', TerminalClientTuiTranscript.MaximumCharacters + 100));
        transcript.Add("recent-message");

        var lines = transcript.CreateLines(40);

        Assert.True(lines.Count <= TerminalClientTuiTranscript.MaximumWrappedLines);
        Assert.Contains(lines, line => line.Contains("recent-message", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(new string('a', 100), StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedSingleTranscriptEntryKeepsItsTailAndTruncationMarker()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', TerminalClientTuiTranscript.MaximumCharacters + 10) + "final-tail");

        var lines = transcript.CreateLines(40);

        Assert.True(lines.Count <= TerminalClientTuiTranscript.MaximumWrappedLines);
        Assert.Contains(lines, line => line.Contains("[Earlier transcript content truncated]", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("final-tail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PastedLineIsDeliveredWithoutInterpretingItAsCommands()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        string? received = null;

        var runTask = host.RunAsync(async _ =>
        {
            received = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("Hola, señor.");
        driver.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Hola, señor.", received);
        Assert.True(driver.Restored);
    }

    [Fact]
    public void SanitizerEscapesTerminalControlSequencesAndPreservesNewLines()
    {
        var normalized = TerminalTextSanitizer.Normalize("hello\u001b]8;url\u0007\nworld\t");

        Assert.Equal("hello\\u001B]8;url\\u0007\nworld\\u0009", normalized);
    }

    [Fact]
    public void SingleLineSanitizerEscapesNewLinesAndCarriageReturns()
    {
        var normalized = TerminalTextSanitizer.NormalizeSingleLine("first\r\nsecond");

        Assert.Equal("first\\u000D\\nsecond", normalized);
    }

    private static async Task WaitForFramesAsync(FakeTerminalDriver driver, int count)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (driver.Frames.Count >= count)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected TUI frames were not rendered.");
    }

    private static async Task WaitForInputRequestAsync(
        TerminalClientTuiConsoleAdapter console,
        string expectedPrompt)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (console.TryGetInputRequest(out var request) && request?.Prompt == expectedPrompt)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"The expected input prompt '{expectedPrompt}' was not published.");
    }

    private static async Task<int> RunPairingInputSequenceAsync(
        TerminalClientTuiConsoleAdapter console,
        CancellationToken cancellationToken)
    {
        var clientId = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
            TerminalInputKind.Line,
            "Private client ID (leave empty to pair): ")));
        if (clientId is null)
        {
            return 2;
        }

        var challenge = await Task.Run(() => console.ReadSecret(new TerminalInputRequest(
            TerminalInputKind.Secret,
            "Administrative pairing challenge: ")));
        if (string.IsNullOrWhiteSpace(challenge))
        {
            return 2;
        }

        var displayName = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
            TerminalInputKind.Line,
            "Private client display name: ")));
        return string.IsNullOrWhiteSpace(displayName) || cancellationToken.IsCancellationRequested ? 2 : 0;
    }

    private sealed class FakeTerminalDriver : ITerminalDriver
    {
        private readonly ConcurrentQueue<TerminalInputEvent> _inputs = new();

        public FakeTerminalDriver(TerminalSize size)
        {
            Size = size;
        }

        public TerminalSize Size { get; set; }

        public List<IReadOnlyList<string>> Frames { get; } = [];

        public bool Restored { get; private set; }

        public int RestoreCount { get; private set; }

        public int InputReadCount { get; private set; }

        public TerminalSize GetSize() => Size;

        public bool TryInitialize() => true;

        public TerminalInputEvent? TryReadInput()
        {
            InputReadCount++;
            return _inputs.TryDequeue(out var input) ? input : null;
        }

        public void Render(IReadOnlyList<string> lines) => Frames.Add(lines.ToList());

        public void Restore()
        {
            Restored = true;
            RestoreCount++;
        }

        public void Enqueue(ConsoleKeyInfo key) => _inputs.Enqueue(new TerminalInputEvent(key, false));

        public void EnqueueEndOfInput() => _inputs.Enqueue(TerminalInputEvent.EndOfInput);

        public void EnqueueCharacters(string value)
        {
            foreach (var character in value)
            {
                _inputs.Enqueue(new TerminalInputEvent(
                    new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false),
                    false));
            }
        }
    }
}
