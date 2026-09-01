using LocalAssistant.TerminalClient;

try
{
    var options = TerminalClientOptions.Parse(args);
    using var httpClient = new HttpClient
    {
        BaseAddress = options.BaseUri,
        Timeout = TerminalClientOptions.RequestTimeout,
    };
    var application = new TerminalClientApplication(
        new PrivateApiClient(httpClient),
        new SystemTerminalConsole(),
        options);

    return await application.RunAsync(CancellationToken.None);
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
