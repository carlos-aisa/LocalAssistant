namespace LocalAssistant.TerminalClient;

internal sealed class TerminalClientStateTextSink : ITerminalClientStateSink
{
    private readonly ITerminalConsole _console;
    private TerminalClientSpokenOutputState? _lastSpokenOutput;
    private bool _wasPlayingVoice;

    public TerminalClientStateTextSink(ITerminalConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public void OnStateChanged(TerminalClientStateSnapshot snapshot)
    {
        switch (snapshot.Lifecycle)
        {
            case TerminalClientLifecycle.Connecting:
                _console.WriteLine("Connecting to the local server...");
                break;
            case TerminalClientLifecycle.Authenticating:
                _console.WriteLine("Authenticating the private client...");
                break;
        }

        var isPlayingVoice = snapshot.Activity == TerminalClientActivity.PlayingVoice;
        if (_lastSpokenOutput == snapshot.SpokenOutput && _wasPlayingVoice == isPlayingVoice)
        {
            return;
        }

        _lastSpokenOutput = snapshot.SpokenOutput;
        _wasPlayingVoice = isPlayingVoice;
        _console.WriteLine(CreateSpokenOutputMessage(snapshot));
    }

    private static string CreateSpokenOutputMessage(TerminalClientStateSnapshot snapshot)
    {
        if (snapshot.Activity == TerminalClientActivity.PlayingVoice)
        {
            return CreateConfigurationMessage(snapshot, "playing");
        }

        if (snapshot.SpokenOutput.Availability == SpokenOutputAvailability.Unavailable)
        {
            return CreateConfigurationMessage(snapshot, "unavailable");
        }

        return CreateConfigurationMessage(snapshot, snapshot.SpokenOutput.IsMuted ? "muted" : "ready");
    }

    private static string CreateConfigurationMessage(
        TerminalClientStateSnapshot snapshot,
        string status) =>
        $"Spoken output: {status}; voice: {snapshot.SpokenOutput.VoiceId ?? "default"}; " +
        $"rate: {snapshot.SpokenOutput.Rate}; volume: {snapshot.SpokenOutput.Volume}.";
}
