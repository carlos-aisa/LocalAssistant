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

        Assert.Contains(driver.Frames, frame => frame.Any(line => line == "Expires: 2026-09-06 12:00 UTC"));
        Assert.Contains(driver.Frames, frame => frame.Any(line => line.Contains("A temporary failure", StringComparison.Ordinal)));
        Assert.Contains(driver.Frames, frame => frame.All(line => line.Length <= 30));
    }

    [Fact]
    public async Task TranscriptIsClippedAndScrollKeysOnlyChangeTheViewport()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(24, 10));
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

        Assert.All(driver.Frames.SelectMany(frame => frame), line => Assert.True(line.Length <= 24));
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

        public int InputReadCount { get; private set; }

        public TerminalSize GetSize() => Size;

        public TerminalInputEvent? TryReadInput()
        {
            InputReadCount++;
            return _inputs.TryDequeue(out var input) ? input : null;
        }

        public void Render(IReadOnlyList<string> lines) => Frames.Add(lines.ToList());

        public void Restore() => Restored = true;

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
