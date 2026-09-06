using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientTuiTests
{
    [Fact]
    public async Task SecretInputIsReturnedToTheWaitingOperationWithoutEnteringTheTranscript()
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
    public void StateSinkCoalescesOrdinarySnapshotsAndPreservesAnErrorTransition()
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

        var published = sink.Drain().ToList();

        Assert.Contains(published, snapshot => snapshot.Error?.Code == "network_error");
        Assert.Equal(ready, published[^1]);
    }

    [Fact]
    public async Task CancelledInputReturnsTheSameEofValueAsTheTextConsole()
    {
        var console = new TerminalClientTuiConsoleAdapter();
        var readTask = Task.Run(() => console.ReadLine(new TerminalInputRequest(
            TerminalInputKind.Line,
            "You: ")));

        await console.WaitForInputAsync();
        console.CancelInput();

        Assert.Null(await readTask);
    }

}
