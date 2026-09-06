namespace LocalAssistant.TerminalClient;

internal interface ITerminalPresentationCapabilities
{
    bool IsInputRedirected { get; }

    bool IsOutputRedirected { get; }

    bool IsErrorRedirected { get; }
}

internal enum TerminalPresentationMode
{
    Plain,
    Tui,
}

internal sealed record TerminalPresentationDecision
{
    private TerminalPresentationDecision(
        TerminalPresentationMode mode,
        string reason,
        ITerminalDriver? driver)
    {
        Mode = mode;
        Reason = reason;
        Driver = driver;
    }

    public TerminalPresentationMode Mode { get; }

    public string Reason { get; }

    public ITerminalDriver? Driver { get; }

    public bool UsesTui => Mode == TerminalPresentationMode.Tui;

    public static TerminalPresentationDecision Plain(string reason) => new(
        TerminalPresentationMode.Plain,
        reason,
        null);

    public static TerminalPresentationDecision Tui(ITerminalDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return new TerminalPresentationDecision(
            TerminalPresentationMode.Tui,
            "interactive_terminal",
            driver);
    }
}

internal static class TerminalPresentationSelector
{
    public static TerminalPresentationDecision Select(
        TerminalClientOptions options,
        ITerminalPresentationCapabilities capabilities,
        Func<ITerminalDriver?> driverFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(driverFactory);

        if (options.ForcePlain)
        {
            return TerminalPresentationDecision.Plain("plain_requested");
        }

        if (capabilities.IsInputRedirected ||
            capabilities.IsOutputRedirected ||
            capabilities.IsErrorRedirected)
        {
            return TerminalPresentationDecision.Plain("redirected");
        }

        ITerminalDriver? driver = null;
        try
        {
            driver = driverFactory();
            if (driver is null)
            {
                return TerminalPresentationDecision.Plain("unsupported_terminal");
            }

            if (!driver.TryInitialize())
            {
                return CreateInitializationFallback(driver);
            }

            var size = driver.GetSize();
            if (size.Width < TerminalClientTuiHost.MinimumWidth ||
                size.Height < TerminalClientTuiHost.MinimumHeight)
            {
                return CreateFallback(driver, "terminal_too_small");
            }
        }
        catch (IOException)
        {
            return CreateInitializationFallback(driver);
        }
        catch (InvalidOperationException)
        {
            return CreateInitializationFallback(driver);
        }
        catch (PlatformNotSupportedException)
        {
            return CreateInitializationFallback(driver);
        }
        catch (Exception) when (driver is not null)
        {
            // An unexpected exception is not reclassified as a compatibility failure, but the
            // probe may already have hidden the cursor or cleared the screen. Honour the
            // "a failed preparation requests restoration exactly once" invariant before the
            // exception continues to the top-level handler in Main.
            RestoreQuietly(driver);
            throw;
        }

        return TerminalPresentationDecision.Tui(driver);
    }

    private static TerminalPresentationDecision CreateInitializationFallback(ITerminalDriver? driver) =>
        CreateFallback(driver, "tui_initialization_failed");

    private static TerminalPresentationDecision CreateFallback(ITerminalDriver? driver, string reason)
    {
        RestoreQuietly(driver);
        return TerminalPresentationDecision.Plain(reason);
    }

    private static void RestoreQuietly(ITerminalDriver? driver)
    {
        try
        {
            driver?.Restore();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
