using System.Runtime.Versioning;

namespace LocalAssistant.TerminalClient;

internal interface ITerminalProgramEnvironment : ITerminalPresentationCapabilities
{
    void RegisterCancellation(Action cancellationHandler);

    void UnregisterCancellation(Action cancellationHandler);

    void WriteError(string value);

    void WriteLine(string value);
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

    public void WriteLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Console.WriteLine(value);
    }
}

internal static class TerminalClientProgram
{
    public static Task<int> Main(string[] args) => RunAsync(
        args,
        TerminalClientConfiguration.Load,
        new SystemTerminalProgramEnvironment(),
        static () => new SystemTerminalDriver(),
        RunDiagnosticsAsync,
        RunPlainApplicationAsync,
        RunTuiApplicationAsync);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<string[], TerminalClientConfigurationResult> loadConfiguration,
        ITerminalProgramEnvironment environment,
        Func<ITerminalDriver?> driverFactory,
        Func<TerminalClientConfigurationResult, ITerminalProgramEnvironment, Func<ITerminalDriver?>, Task<int>>
            runDiagnosticsAsync,
        Func<TerminalClientOptions, CancellationToken, Task<int>> runPlainAsync,
        Func<TerminalClientOptions, ITerminalDriver, CancellationToken, Task<int>> runTuiAsync)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loadConfiguration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(driverFactory);
        ArgumentNullException.ThrowIfNull(runDiagnosticsAsync);
        ArgumentNullException.ThrowIfNull(runPlainAsync);
        ArgumentNullException.ThrowIfNull(runTuiAsync);

        try
        {
            if (TerminalClientCommandLine.RequestsHelp(args))
            {
                environment.WriteLine(TerminalClientCommandLineText.Help());
                return 0;
            }

            if (TerminalClientCommandLine.RequestsVersion(args))
            {
                environment.WriteLine(TerminalClientCommandLineText.Version());
                return 0;
            }

            var configuration = loadConfiguration(args);
            if (TerminalClientCommandLine.RequestsDiagnostics(args))
            {
                return await runDiagnosticsAsync(configuration, environment, driverFactory);
            }

            return await RunConfiguredAsync(
                configuration.Options,
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

    private static async Task<int> RunDiagnosticsAsync(
        TerminalClientConfigurationResult configuration,
        ITerminalProgramEnvironment environment,
        Func<ITerminalDriver?> driverFactory)
    {
        using var cancellationSource = new CancellationTokenSource();
        Action cancellationHandler = cancellationSource.Cancel;
        environment.RegisterCancellation(cancellationHandler);

        try
        {
            var appSettingsPath = Path.Combine(
                AppContext.BaseDirectory,
                TerminalClientConfiguration.AppSettingsFileName);
            var credentialStore = new DpapiPrivateClientCredentialStore();

            var report = await TerminalDiagnosticsProbe.BuildAsync(
                configuration,
                appSettingsPath,
                File.Exists(appSettingsPath),
                cancellationToken => ProbeApiHealthAsync(configuration.Options.BaseUri, cancellationToken),
                credentialStore.ReadLocalStateSectionAsync,
                environment,
                driverFactory,
                ProbeSpokenOutputForDiagnostics,
                cancellationSource.Token);
            environment.WriteLine(report.ToText());
            return 0;
        }
        finally
        {
            environment.UnregisterCancellation(cancellationHandler);
        }
    }

    private static async Task<TerminalDiagnosticsApiProbeResult> ProbeApiHealthAsync(
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TerminalDiagnosticsProbe.ApiProbeTimeout,
        };

        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            return new TerminalDiagnosticsApiProbeResult(
                response.IsSuccessStatusCode
                    ? TerminalDiagnosticsApiHealth.Reachable
                    : TerminalDiagnosticsApiHealth.UnexpectedStatus,
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout expired; distinguished from a caller-requested cancellation,
            // which instead propagates so Ctrl+C still ends the run.
            return new TerminalDiagnosticsApiProbeResult(TerminalDiagnosticsApiHealth.Timeout, null);
        }
        catch (HttpRequestException)
        {
            return new TerminalDiagnosticsApiProbeResult(TerminalDiagnosticsApiHealth.Unreachable, null);
        }
    }

    private static TerminalDiagnosticsSpokenOutputSection ProbeSpokenOutputForDiagnostics() =>
        OperatingSystem.IsWindows()
            ? ProbeWindowsSpokenOutputForDiagnostics()
            : TerminalDiagnosticsSpokenOutputSection.NotWindows;

    [SupportedOSPlatform("windows")]
    private static TerminalDiagnosticsSpokenOutputSection ProbeWindowsSpokenOutputForDiagnostics()
    {
        try
        {
            var count = WindowsSpeechSynthesizer.CountEnabledVoices();
            return new TerminalDiagnosticsSpokenOutputSection(
                IsWindows: true,
                SubsystemInitializes: true,
                EnabledVoiceCount: count,
                IsAvailable: count > 0);
        }
        catch (Exception)
        {
            return new TerminalDiagnosticsSpokenOutputSection(
                IsWindows: true,
                SubsystemInitializes: false,
                EnabledVoiceCount: null,
                IsAvailable: false);
        }
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
