namespace LocalAssistant.TerminalClient;

internal sealed class TerminalClientStateTextSink : ITerminalClientStateSink
{
    private readonly ITerminalConsole _console;

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
    }
}
