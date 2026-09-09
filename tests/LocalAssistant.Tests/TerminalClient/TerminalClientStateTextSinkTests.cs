using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientStateTextSinkTests
{
    [Fact]
    public void SinkReportsSpokenOutputChangesWithoutDuplicatingUnchangedState()
    {
        using var console = new ScriptedTerminalConsole([]);
        var sink = new TerminalClientStateTextSink(console);
        var ready = TerminalClientStateSnapshot.Initial with
        {
            Lifecycle = TerminalClientLifecycle.Ready,
            Provider = "fake",
        };

        sink.OnStateChanged(ready);
        sink.OnStateChanged(ready);
        sink.OnStateChanged(ready with
        {
            SpokenOutput = new TerminalClientSpokenOutputState(
                SpokenOutputAvailability.Ready,
                IsMuted: false),
        });
        sink.OnStateChanged(ready with
        {
            SpokenOutput = new TerminalClientSpokenOutputState(
                SpokenOutputAvailability.Ready,
                IsMuted: false),
            Activity = TerminalClientActivity.PlayingVoice,
        });
        sink.OnStateChanged(ready with
        {
            SpokenOutput = new TerminalClientSpokenOutputState(
                SpokenOutputAvailability.Ready,
                IsMuted: true),
        });

        Assert.Equal(1, Count(console.Output, "Spoken output: unavailable;"));
        Assert.Equal(1, Count(console.Output, "Spoken output: ready;"));
        Assert.Equal(1, Count(console.Output, "Spoken output: playing;"));
        Assert.Equal(1, Count(console.Output, "Spoken output: muted;"));
        Assert.Contains("rate: 0; volume: 100", console.Output, StringComparison.Ordinal);
    }

    private static int Count(string text, string value) => text.Split(value).Length - 1;
}
