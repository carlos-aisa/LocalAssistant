namespace LocalAssistant.TerminalClient;

internal interface ITerminalPresentationCapabilities
{
    bool IsInputRedirected { get; }

    bool IsOutputRedirected { get; }

    bool SupportsInteractiveTui { get; }
}

internal sealed class SystemTerminalPresentationCapabilities : ITerminalPresentationCapabilities
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public bool SupportsInteractiveTui =>
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected &&
        !Console.IsErrorRedirected;
}

internal static class TerminalPresentationSelector
{
    public static bool UseTui(TerminalClientOptions options, ITerminalPresentationCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        return !options.ForcePlain &&
            !capabilities.IsInputRedirected &&
            !capabilities.IsOutputRedirected &&
            capabilities.SupportsInteractiveTui;
    }
}
