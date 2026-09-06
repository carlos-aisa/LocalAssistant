namespace LocalAssistant.TerminalClient;

internal static class TerminalClientProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = TerminalClientOptions.Parse(args);
            using var cancellationSource = new CancellationTokenSource();
            ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationSource.Cancel();
            };
            Console.CancelKeyPress += cancellationHandler;
            using var httpClient = new HttpClient
            {
                BaseAddress = options.BaseUri,
                Timeout = TerminalClientOptions.RequestTimeout,
            };
            try
            {
                if (TerminalPresentationSelector.UseTui(options, new SystemTerminalPresentationCapabilities()))
                {
                    var console = new TerminalClientTuiConsoleAdapter();
                    var stateSink = new TerminalClientTuiStateSink();
                    var tuiApplication = new TerminalClientApplication(
                        new PrivateApiClient(httpClient),
                        console,
                        options,
                        new DpapiPrivateClientCredentialStore(),
                        stateSink);
                    return await new TerminalClientTuiHost(console, stateSink)
                        .RunAsync(tuiApplication, cancellationSource.Token);
                }

                var application = new TerminalClientApplication(
                    new PrivateApiClient(httpClient),
                    new SystemTerminalConsole(),
                    options,
                    new DpapiPrivateClientCredentialStore());
                return await application.RunAsync(cancellationSource.Token);
            }
            finally
            {
                Console.CancelKeyPress -= cancellationHandler;
            }
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Client error: {exception.Message}");
            return 1;
        }
    }
}
