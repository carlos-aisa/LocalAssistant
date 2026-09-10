namespace LocalAssistant.TerminalClient;

public sealed record TerminalClientOptions(
    Uri BaseUri,
    string Provider,
    string Scenario,
    TimeSpan RequestTimeout,
    bool ForcePlain = false)
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(4);

    public static TerminalClientOptions Parse(string[] args) =>
        TerminalClientConfiguration.LoadFromCommandLine(args).Options;
}
