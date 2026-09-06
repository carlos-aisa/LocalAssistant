namespace LocalAssistant.TerminalClient;

internal interface ITerminalPresentationCapabilities
{
    bool IsInputRedirected { get; }

    bool IsOutputRedirected { get; }

    bool SupportsInteractiveTui { get; }
}

internal enum TerminalPresentationMode
{
    Plain,
    Tui,
}

internal sealed record TerminalPresentationDecision(TerminalPresentationMode Mode, string Reason)
{
    public bool UsesTui => Mode == TerminalPresentationMode.Tui;
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
    public static TerminalPresentationDecision Select(
        TerminalClientOptions options,
        ITerminalPresentationCapabilities capabilities,
        ITerminalDriver? driver)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (options.ForcePlain)
        {
            return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "plain_requested");
        }

        if (capabilities.IsInputRedirected || capabilities.IsOutputRedirected)
        {
            return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "redirected");
        }

        if (!capabilities.SupportsInteractiveTui || driver is null)
        {
            return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "unsupported_terminal");
        }

        try
        {
            if (!driver.TryInitialize())
            {
                driver.Restore();
                return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "tui_initialization_failed");
            }

            var size = driver.GetSize();
            if (size.Width < TerminalClientTuiHost.MinimumWidth || size.Height < TerminalClientTuiHost.MinimumHeight)
            {
                driver.Restore();
                return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "terminal_too_small");
            }
        }
        catch (IOException)
        {
            driver.Restore();
            return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "tui_initialization_failed");
        }
        catch (InvalidOperationException)
        {
            driver.Restore();
            return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "tui_initialization_failed");
        }
        catch (PlatformNotSupportedException)
        {
            driver.Restore();
            return new TerminalPresentationDecision(TerminalPresentationMode.Plain, "tui_initialization_failed");
        }

        return new TerminalPresentationDecision(TerminalPresentationMode.Tui, "interactive_terminal");
    }
}
