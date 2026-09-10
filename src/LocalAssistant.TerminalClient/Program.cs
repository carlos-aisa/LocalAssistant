namespace LocalAssistant.TerminalClient;

internal interface ITerminalProgramEnvironment : ITerminalPresentationCapabilities
{
    void RegisterCancellation(Action cancellationHandler);

    void UnregisterCancellation(Action cancellationHandler);

    void WriteError(string value);
}

internal sealed class SystemTerminalProgramEnvironment : ITerminalProgramEnvironment
{
    private readonly Dictionary<Action, ConsoleCancelEventHandler> _cancellationHandlers = [];

    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public bool IsErrorRedirected => Console.IsErrorRedirected;

    public void RegisterCancellation(Action cancellationHandler)
    {
        ArgumentNullException.ThrowIfNull(cancellationHandler);
        var consoleHandler = new ConsoleCancelEventHandler((_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationHandler();
        });
        _cancellationHandlers.Add(cancellationHandler, consoleHandler);
        Console.CancelKeyPress += consoleHandler;
    }

    public void UnregisterCancellation(Action cancellationHandler)
    {
        ArgumentNullException.ThrowIfNull(cancellationHandler);
        if (_cancellationHandlers.Remove(cancellationHandler, out var consoleHandler))
        {
            Console.CancelKeyPress -= consoleHandler;
        }
    }

    public void WriteError(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Console.Error.WriteLine(value);
    }
}

internal static class TerminalClientProgram
{
    public static Task<int> Main(string[] args) => RunAsync(
        args,
        TerminalClientConfiguration.Load,
        new SystemTerminalProgramEnvironment(),
        static () => new SystemTerminalDriver(),
        RunPlainApplicationAsync,
        RunTuiApplicationAsync);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<string[], TerminalClientConfigurationResult> loadConfiguration,
        ITerminalProgramEnvironment environment,
        Func<ITerminalDriver?> driverFactory,
        Func<TerminalClientOptions, CancellationToken, Task<int>> runPlainAsync,
        Func<TerminalClientOptions, ITerminalDriver, CancellationToken, Task<int>> runTuiAsync)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loadConfiguration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(driverFactory);
        ArgumentNullException.ThrowIfNull(runPlainAsync);
        ArgumentNullException.ThrowIfNull(runTuiAsync);

        try
        {
            var options = loadConfiguration(args).Options;
            return await RunConfiguredAsync(
                options,
                environment,
                driverFactory,
                runPlainAsync,
                runTuiAsync);
        }
        catch (ArgumentException exception)
        {
            environment.WriteError($"Configuration error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            environment.WriteError($"Client error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunConfiguredAsync(
        TerminalClientOptions options,
        ITerminalProgramEnvironment environment,
        Func<ITerminalDriver?> driverFactory,
        Func<TerminalClientOptions, CancellationToken, Task<int>> runPlainAsync,
        Func<TerminalClientOptions, ITerminalDriver, CancellationToken, Task<int>> runTuiAsync)
    {
        using var cancellationSource = new CancellationTokenSource();
        Action cancellationHandler = cancellationSource.Cancel;
        environment.RegisterCancellation(cancellationHandler);

        try
        {
            var presentation = TerminalPresentationSelector.Select(
                options,
                environment,
                driverFactory);
            if (presentation.UsesTui)
            {
                var driver = presentation.Driver!;
                try
                {
                    return await runTuiAsync(options, driver, cancellationSource.Token);
                }
                finally
                {
                    // The selector transfers restoration to the TUI host, but the presentation
                    // graph (HttpClient, console, sink, application) is built before the host's
                    // finally can run. This outer guard covers a failure in that window; because
                    // SystemTerminalDriver.Restore is idempotent it is a no-op when the host has
                    // already restored the driver.
                    driver.Restore();
                }
            }

            WriteFallbackNotice(environment, presentation.Reason);
            return await runPlainAsync(options, cancellationSource.Token);
        }
        finally
        {
            environment.UnregisterCancellation(cancellationHandler);
        }
    }

    private static void WriteFallbackNotice(ITerminalProgramEnvironment environment, string reason)
    {
        if (reason is "plain_requested" or "redirected")
        {
            return;
        }

        environment.WriteError($"TUI unavailable ({reason}); using plain mode.");
    }

    private static async Task<int> RunPlainApplicationAsync(
        TerminalClientOptions options,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(options);
        var console = new SystemTerminalConsole();
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            options,
            new DpapiPrivateClientCredentialStore(),
            new TerminalClientStateTextSink(console),
            WindowsSpokenOutputFactory.Create(SpokenOutputPreferences.Default));
        return await application.RunAsync(cancellationToken);
    }

    private static async Task<int> RunTuiApplicationAsync(
        TerminalClientOptions options,
        ITerminalDriver driver,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(options);
        var console = new TerminalClientTuiConsoleAdapter();
        var stateSink = new TerminalClientTuiStateSink();
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            options,
            new DpapiPrivateClientCredentialStore(),
            stateSink,
            WindowsSpokenOutputFactory.Create(SpokenOutputPreferences.Default));
        return await new TerminalClientTuiHost(console, stateSink, driver)
            .RunAsync(application, cancellationToken);
    }

    internal static HttpClient CreateHttpClient(TerminalClientOptions options) => new()
    {
        BaseAddress = options.BaseUri,
        Timeout = options.RequestTimeout,
    };
}
