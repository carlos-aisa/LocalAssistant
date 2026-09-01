namespace LocalAssistant.TerminalClient;

public sealed class TerminalClientApplication
{
    private readonly PrivateApiClient _apiClient;
    private readonly ITerminalConsole _console;
    private readonly TerminalClientOptions _options;

    public TerminalClientApplication(
        PrivateApiClient apiClient,
        ITerminalConsole console,
        TerminalClientOptions options)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var health = await _apiClient.CheckHealthAsync(cancellationToken);
        if (!health.IsSuccess)
        {
            WriteError(health.Error!);
            return 1;
        }

        _console.Write("Private client ID: ");
        var clientId = _console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _console.WriteLine("A private client ID is required.");
            return 2;
        }

        _console.Write("Private client credential: ");
        var credential = _console.ReadSecret();
        if (string.IsNullOrWhiteSpace(credential))
        {
            _console.WriteLine("A private client credential is required.");
            return 2;
        }

        var session = await _apiClient.CreateSessionAsync(clientId, credential, cancellationToken);
        credential = string.Empty;
        if (!session.IsSuccess)
        {
            WriteError(session.Error!);
            return 1;
        }

        var accessToken = session.Value!.AccessToken;
        try
        {
            ShowStartup();
            return await ProcessMessagesAsync(accessToken, cancellationToken);
        }
        finally
        {
            accessToken = string.Empty;
        }
    }

    private async Task<int> ProcessMessagesAsync(string accessToken, CancellationToken cancellationToken)
    {
        Guid? conversationId = null;
        while (true)
        {
            _console.Write("You: ");
            var message = _console.ReadLine();
            if (message is null)
            {
                return 0;
            }

            message = message.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var response = await _apiClient.SendMessageAsync(
                accessToken,
                new SendMessageRequest(message, conversationId, _options.Provider, _options.Scenario),
                cancellationToken);
            if (!response.IsSuccess)
            {
                WriteError(response.Error!);
                continue;
            }

            conversationId = response.Value!.ConversationId;
            ShowResponse(response.Value);
            if (response.Value.Confirmation is not null)
            {
                _console.WriteLine(
                    "A tool confirmation is pending. This client increment cannot resolve it yet.");
                return 1;
            }
        }
    }

    private void ShowStartup()
    {
        _console.WriteLine("LocalAssistant terminal client");
        _console.WriteLine($"Server: {_options.BaseUri}");
        if (_options.Provider == "fake")
        {
            _console.WriteLine($"Provider: fake (scenario: {_options.Scenario})");
            return;
        }

        _console.WriteLine("Provider: ollama (the server configures the model)");
    }

    private void ShowResponse(ConversationResponse response)
    {
        _console.WriteLine($"Conversation: {response.ConversationId}");
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            _console.WriteLine($"Assistant: {response.Content}");
        }

        _console.WriteLine($"Iterations: {response.Iterations}");
        foreach (var tool in response.Tools)
        {
            var outcome = tool.Succeeded ? "completed" : "failed";
            _console.WriteLine($"Tool: {tool.ToolName} ({outcome})");
        }

        if (response.Error is not null)
        {
            _console.WriteLine($"Conversation error: {response.Error.Code}");
        }
    }

    private void WriteError(ClientError error)
    {
        var suffix = error.IsUncertain ? " The server may have received the turn; it was not retried." : string.Empty;
        _console.WriteLine($"Error ({error.Code}): {error.Message}{suffix}");
    }
}
