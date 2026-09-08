using System.Collections.Concurrent;
using System.Globalization;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientTuiTests
{
    private const char EscControlChar = (char)0x1B;

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
        await WaitForFrameContainingAsync(driver, "Second:");
        driver.EnqueueCharacters("next");
        driver.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public async Task ConsecutiveInputPromptsRenderWithoutRequiringAKeystroke()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Private client ID (leave empty to pair): ")));
            await Task.Run(() => console.ReadSecret(new TerminalInputRequest(
                TerminalInputKind.Secret,
                "Private client credential: ")));
            return 0;
        }, CancellationToken.None);

        await WaitForFrameContainingAsync(driver, "Private client ID (leave empty to pair):");

        // Advance to the next prompt without a keystroke, state snapshot, or transcript write:
        // only the pending input request changes.
        console.CompleteInput("client-123");

        await WaitForFrameContainingAsync(driver, "Private client credential:");

        console.CompleteInput("secret-value");
        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
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
        Assert.DoesNotContain(driver.Frames[^1], line => line.Contains('*'));
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

        var frame = driver.Frames[^1];
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

    [Fact]
    public async Task TextTypedWithoutAnActivePromptIsNotAttachedToTheNextPrompt()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        string? received = null;
        var promptGate = new TaskCompletionSource();

        var runTask = host.RunAsync(async _ =>
        {
            await promptGate.Task;
            received = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Type approve, reject, or cancel: ")));
            return 0;
        }, CancellationToken.None);

        driver.EnqueueCharacters("approve");
        driver.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        await WaitForInputDrainedAsync(driver);

        promptGate.SetResult();
        await WaitForFrameContainingAsync(driver, "Type approve, reject, or cancel:");
        driver.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(string.Empty, received);
        Assert.DoesNotContain(
            driver.Frames.SelectMany(frame => frame),
            line => line.Contains("cancel: approve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DriverDeliveredCtrlCCancelsTheApplicationAndClosesTheChannel()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var cancelled = false;

        var runTask = host.RunAsync(async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            return 2;
        }, CancellationToken.None);

        driver.Enqueue(new ConsoleKeyInfo((char)0x03, ConsoleKey.C, false, false, control: true));

        Assert.Equal(2, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(cancelled);
        Assert.True(driver.Restored);
    }

    [Fact]
    public async Task CtrlDClosesTheChannelWithoutCancellingTheApplicationToken()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);
        var tokenWasCancelled = true;

        var runTask = host.RunAsync(async token =>
        {
            var line = await Task.Run(() => console.ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: ")));
            tokenWasCancelled = token.IsCancellationRequested;
            return line is null ? 7 : 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.Enqueue(new ConsoleKeyInfo((char)0x04, ConsoleKey.D, false, false, control: true));

        Assert.Equal(7, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(tokenWasCancelled);
    }

    [Fact]
    public async Task BackspaceShrinksTheInputShownInTheFrame()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "You: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("abcd");
        await WaitForFrameWithLineAsync(driver, "You: abcd");
        driver.Enqueue(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false));
        await WaitForFrameWithLineAsync(driver, "You: abc");

        console.CancelInput();
        await runTask;
    }

    [Fact]
    public async Task PageUpAndPageDownScrollWithoutCompletingThePrompt()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(40, 10));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            var input = await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "You: ")));
            return input is null ? 9 : 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        for (var index = 0; index < 12; index++)
        {
            console.WriteConversationMessage("Assistant", $"line-{index:D2}");
        }

        await WaitForFramesAsync(driver, 2);
        driver.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
        driver.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false));
        await WaitForInputDrainedAsync(driver);

        Assert.False(runTask.IsCompleted);
        driver.EnqueueEndOfInput();
        Assert.Equal(9, await runTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RepeatedPageUpKeepsScrollingResponsiveAfterResize()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(40, 12));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "You: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        for (var index = 0; index < 30; index++)
        {
            console.WriteConversationMessage("Assistant", $"row-{index:D2}");
        }

        await WaitForFrameContainingAsync(driver, "row-29");
        for (var index = 0; index < 200; index++)
        {
            driver.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false));
        }

        await WaitForInputDrainedAsync(driver);
        await WaitForFrameContainingAsync(driver, "row-00");

        driver.Size = new TerminalSize(40, 8);
        await WaitForFrameContainingAsync(driver, "row-04");
        var framesBefore = driver.Frames.Count;
        driver.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false));
        await WaitForFramesAsync(driver, framesBefore + 1);

        // One PageDown moves the viewport window toward newer content by exactly the page
        // size. Without the offset clamp it would be astronomically large and PageDown would
        // have no visible effect.
        await WaitForFrameContainingAsync(driver, "row-14");

        console.CancelInput();
        await runTask;
    }

    [Fact]
    public async Task CompactViewportKeepsConfirmationInputAndTheResizeHint()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var sink = new TerminalClientTuiStateSink();
        var driver = new FakeTerminalDriver(new TerminalSize(20, 6));
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
                "create_reminder",
                DateTimeOffset.Parse("2026-09-07T12:00:00+00:00", CultureInfo.InvariantCulture)),
            Error = new TerminalClientOperationError(
                TerminalClientErrorSeverity.Recoverable,
                true,
                "temporary",
                "temporary failure",
                "turn"),
        });

        await console.WaitForInputAsync();
        await WaitForFramesAsync(driver, 2);
        console.CancelInput();
        await runTask;

        var frame = driver.Frames[^1];
        Assert.Contains(frame, line => line.StartsWith("CONFIRM:", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.StartsWith("You:", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.StartsWith("Terminal too small", StringComparison.Ordinal));
        Assert.All(frame, line => Assert.True(line.Length <= 20));
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(1, 1)]
    public async Task SubMinimumViewportRendersWithoutThrowingOrOverflowing(int width, int height)
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var sink = new TerminalClientTuiStateSink();
        var driver = new FakeTerminalDriver(new TerminalSize(width, height));
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
                "create_reminder",
                DateTimeOffset.Parse("2026-09-07T12:00:00+00:00", CultureInfo.InvariantCulture)),
            Error = new TerminalClientOperationError(
                TerminalClientErrorSeverity.Recoverable,
                true,
                "network\nerror",
                "failed" + EscControlChar + "[2J",
                "turn"),
        });

        await console.WaitForInputAsync();
        await WaitForFramesAsync(driver, 2);
        console.CancelInput();
        await runTask;

        Assert.All(driver.Frames.SelectMany(frame => frame), line =>
        {
            Assert.True(line.Length <= width);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain(EscControlChar, line);
        });
    }

    [Fact]
    public async Task LongLineInputShowsItsActiveEndWithATruncationMarker()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(20, 10));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "You: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("0123456789abcdefghijZ");
        await WaitForFrameContainingAsync(driver, "Z");

        var inputLine = Assert.Single(driver.Frames[^1], line => line.Contains('Z', StringComparison.Ordinal));
        Assert.True(inputLine.Length <= 20);
        Assert.StartsWith("…", inputLine);
        Assert.EndsWith("Z", inputLine);
        Assert.DoesNotContain("012345", inputLine);

        console.CancelInput();
        await runTask;
    }

    [Fact]
    public async Task LongSecretInputShowsOnlyAMaskedTail()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(16, 10));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadSecret(new TerminalInputRequest(TerminalInputKind.Secret, "Secret: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("private-value-1234567890");
        await WaitForFramesAsync(driver, 2);

        Assert.DoesNotContain(
            driver.Frames.SelectMany(frame => frame),
            line => line.Contains("private-value", StringComparison.Ordinal));
        Assert.Contains(driver.Frames[^1], line => line.Contains('*', StringComparison.Ordinal));

        console.CancelInput();
        await runTask;
    }

    [Fact]
    public async Task ResizeWhileAwaitingASecretNeverLeaksTheSecret()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadSecret(new TerminalInputRequest(TerminalInputKind.Secret, "Challenge: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        driver.EnqueueCharacters("top-secret-challenge");
        await WaitForFramesAsync(driver, 2);
        driver.Size = new TerminalSize(30, 6);
        await WaitForFramesAsync(driver, 3);
        driver.Size = new TerminalSize(80, 20);
        await WaitForFramesAsync(driver, 4);

        Assert.DoesNotContain(
            driver.Frames.SelectMany(frame => frame),
            line => line.Contains("top-secret", StringComparison.Ordinal));

        console.CancelInput();
        await runTask;
    }

    [Fact]
    public async Task EveryRenderedLineIsOneWidthBoundRowEvenWithHostileMetadata()
    {
        foreach (var width in new[] { 12, 20, 40, 80 })
        {
            var console = new TerminalClientTuiConsoleAdapter();
            var sink = new TerminalClientTuiStateSink();
            var driver = new FakeTerminalDriver(new TerminalSize(width, 10));
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
                    "tool\r\n" + EscControlChar + "]8;evilname",
                    DateTimeOffset.Parse("2026-09-07T12:00:00+00:00", CultureInfo.InvariantCulture)),
                Error = new TerminalClientOperationError(
                    TerminalClientErrorSeverity.Recoverable,
                    true,
                    "code" + EscControlChar + "[2J\r\n",
                    "message\nwith controls",
                    "turn"),
            });

            await console.WaitForInputAsync();
            await WaitForFramesAsync(driver, 2);
            console.CancelInput();
            await runTask;

            Assert.All(driver.Frames.SelectMany(frame => frame), line =>
            {
                Assert.True(line.Length <= width, $"line '{line}' exceeds width {width}");
                Assert.DoesNotContain('\n', line);
                Assert.DoesNotContain(EscControlChar, line);
            });
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    public async Task PromptLongerThanWidthNeverRendersAnEmptyInputLine(int width)
    {
        const string prompt = "Private client ID (leave empty to pair): ";
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(width, 8));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, prompt)));
            return 0;
        }, CancellationToken.None);

        // Empty buffer: a recognizable head of the prompt is visible, never a blank line.
        await WaitForFrameContainingAsync(driver, "Private client ID");
        var empty = InputRow(driver.Frames[^1]);
        Assert.False(string.IsNullOrEmpty(empty));
        Assert.True(empty.Length <= width);
        Assert.StartsWith("Private client ID", empty, StringComparison.Ordinal);

        // Once the user types past the width, the active tail of the value is shown instead.
        driver.EnqueueCharacters("0123456789abcdefghijklmnopqrstuvwxyz-longer-than-any-width-Z");
        await WaitForFrameContainingAsync(driver, "-Z");
        var typed = InputRow(driver.Frames[^1]);
        Assert.True(typed.Length <= width);
        Assert.StartsWith("…", typed, StringComparison.Ordinal);
        Assert.EndsWith("Z", typed, StringComparison.Ordinal);

        console.CancelInput();
        await runTask;
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    public async Task LongSecretPromptShowsThePromptHeadEmptyAndOnlyAMaskedTailWhenTyping(int width)
    {
        const string prompt = "Administrative pairing challenge: ";
        var console = new TerminalClientTuiConsoleAdapter();
        var driver = new FakeTerminalDriver(new TerminalSize(width, 8));
        var host = new TerminalClientTuiHost(console, new TerminalClientTuiStateSink(), driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadSecret(new TerminalInputRequest(TerminalInputKind.Secret, prompt)));
            return 0;
        }, CancellationToken.None);

        await WaitForFrameContainingAsync(driver, "Administrative");
        var empty = InputRow(driver.Frames[^1]);
        Assert.StartsWith("Administrative", empty, StringComparison.Ordinal);
        Assert.DoesNotContain('*', empty);

        driver.EnqueueCharacters("s3cr3t-challenge-value-0123456789");
        await WaitForFrameAsync(driver, frame => InputRow(frame).Contains('*'));
        var masked = InputRow(driver.Frames[^1]);
        Assert.True(masked.Length <= width);
        Assert.All(masked.TrimStart('…'), c => Assert.Equal('*', c));
        Assert.DoesNotContain(
            driver.Frames.SelectMany(frame => frame),
            line => line.Contains("s3cr3t", StringComparison.Ordinal));

        console.CancelInput();
        await runTask;
    }

    [Fact]
    public async Task PrioritySnapshotDrainedAfterAResizeIsRenderedAtTheNewSize()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var sink = new TerminalClientTuiStateSink();
        var driver = new FakeTerminalDriver(new TerminalSize(80, 20));
        var host = new TerminalClientTuiHost(console, sink, driver);

        var runTask = host.RunAsync(async _ =>
        {
            await Task.Run(() => console.ReadLine(new TerminalInputRequest(TerminalInputKind.Line, "You: ")));
            return 0;
        }, CancellationToken.None);

        await console.WaitForInputAsync();
        await WaitForFramesAsync(driver, 1);

        // Shrink and publish a priority snapshot in the same step, before DetectResize runs.
        driver.Size = new TerminalSize(24, 8);
        sink.OnStateChanged(TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
            Activity = TerminalClientActivity.AwaitingConfirmation,
            PendingConfirmation = new TerminalClientPendingConfirmation(
                "create_reminder",
                DateTimeOffset.Parse("2026-09-07T12:00:00+00:00", CultureInfo.InvariantCulture)),
            Error = new TerminalClientOperationError(
                TerminalClientErrorSeverity.Recoverable, true, "boom", "everything is on fire right now", "turn"),
        });

        await WaitForFrameContainingAsync(driver, "CONFIRM");
        console.CancelInput();
        await runTask;

        var confirmFrames = driver.Frames
            .Where(frame => frame.Any(line => line.Contains("CONFIRM", StringComparison.Ordinal)))
            .ToArray();
        Assert.NotEmpty(confirmFrames);
        Assert.All(confirmFrames.SelectMany(frame => frame),
            line => Assert.True(line.Length <= 24, $"line '{line}' exceeds the resized width 24"));
    }

    private static string InputRow(IReadOnlyList<string> frame) =>
        frame.FirstOrDefault(line =>
            !line.StartsWith("CONFIRM:", StringComparison.Ordinal) &&
            !line.StartsWith("ERROR", StringComparison.Ordinal) &&
            !line.StartsWith("State:", StringComparison.Ordinal) &&
            !line.StartsWith("Terminal too small", StringComparison.Ordinal))
        ?? string.Empty;

    private static Task WaitForFrameAsync(
        FakeTerminalDriver driver,
        Func<IReadOnlyList<string>, bool> framePredicate) =>
        driver.WaitForFrameAsync(frames => frames.Any(framePredicate)).WaitAsync(HangGuard);

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    private static async Task WaitForInputDrainedAsync(FakeTerminalDriver driver)
    {
        while (driver.HasPendingInput)
        {
            await driver.WhenInputChangedAsync().WaitAsync(HangGuard);
        }
    }

    private static Task WaitForFrameWithLineAsync(FakeTerminalDriver driver, string line) =>
        driver.WaitForFrameAsync(frames => frames.Any(frame => frame.Contains(line))).WaitAsync(HangGuard);

    private static Task WaitForFramesAsync(FakeTerminalDriver driver, int count) =>
        driver.WaitForFrameAsync(frames => frames.Count >= count).WaitAsync(HangGuard);

    private static Task WaitForFrameContainingAsync(FakeTerminalDriver driver, string fragment) =>
        driver.WaitForFrameAsync(frames =>
            frames.Any(frame => frame.Any(line => line.Contains(fragment, StringComparison.Ordinal))))
            .WaitAsync(HangGuard);

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

    /// <summary>
    /// Thread-safe fake driver with observable signals: tests await a frame predicate or
    /// input consumption instead of polling. Timeouts are hang protection only.
    /// </summary>
    private sealed class FakeTerminalDriver : ITerminalDriver
    {
        private readonly ConcurrentQueue<TerminalInputEvent> _inputs = new();
        private readonly object _sync = new();
        private readonly List<IReadOnlyList<string>> _frames = [];
        private readonly List<(Func<IReadOnlyList<IReadOnlyList<string>>, bool> Predicate, TaskCompletionSource Signal)> _frameWaiters = [];
        private TaskCompletionSource _inputConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TerminalSize _size;
        private int _restoreCount;
        private int _inputReadCount;

        public FakeTerminalDriver(TerminalSize size) => _size = size;

        public TerminalSize Size
        {
            get { lock (_sync) { return _size; } }
            set { lock (_sync) { _size = value; } }
        }

        public IReadOnlyList<IReadOnlyList<string>> Frames
        {
            get { lock (_sync) { return _frames.ToArray(); } }
        }

        public bool Restored => Volatile.Read(ref _restoreCount) > 0;

        public int RestoreCount => Volatile.Read(ref _restoreCount);

        public int InputReadCount => Volatile.Read(ref _inputReadCount);

        public bool HasPendingInput => !_inputs.IsEmpty;

        public TerminalSize GetSize()
        {
            lock (_sync) { return _size; }
        }

        public bool TryInitialize() => true;

        public TerminalInputEvent? TryReadInput()
        {
            Interlocked.Increment(ref _inputReadCount);
            if (!_inputs.TryDequeue(out var input))
            {
                return null;
            }

            TaskCompletionSource consumed;
            lock (_sync)
            {
                consumed = _inputConsumed;
                _inputConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            consumed.TrySetResult();
            return input;
        }

        public void Render(IReadOnlyList<string> lines)
        {
            var frame = lines.ToArray();
            var ready = new List<TaskCompletionSource>();
            lock (_sync)
            {
                _frames.Add(frame);
                var snapshot = (IReadOnlyList<IReadOnlyList<string>>)_frames.ToArray();
                for (var index = _frameWaiters.Count - 1; index >= 0; index--)
                {
                    if (_frameWaiters[index].Predicate(snapshot))
                    {
                        ready.Add(_frameWaiters[index].Signal);
                        _frameWaiters.RemoveAt(index);
                    }
                }
            }

            foreach (var signal in ready)
            {
                signal.TrySetResult();
            }
        }

        public void Restore() => Interlocked.Increment(ref _restoreCount);

        public Task WaitForFrameAsync(Func<IReadOnlyList<IReadOnlyList<string>>, bool> predicate)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (predicate(_frames.ToArray()))
                {
                    return Task.CompletedTask;
                }

                _frameWaiters.Add((predicate, signal));
            }

            return signal.Task;
        }

        public Task WhenInputChangedAsync()
        {
            lock (_sync)
            {
                return _inputs.IsEmpty ? Task.CompletedTask : _inputConsumed.Task;
            }
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
