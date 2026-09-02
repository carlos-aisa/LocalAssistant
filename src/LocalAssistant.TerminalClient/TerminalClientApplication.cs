namespace LocalAssistant.TerminalClient;

public sealed class TerminalClientApplication
{
    private readonly PrivateApiClient _apiClient;
    private readonly ITerminalConsole _console;
    private readonly TerminalClientOptions _options;
    private readonly IPrivateClientCredentialStore _credentialStore;

    public TerminalClientApplication(
        PrivateApiClient apiClient,
        ITerminalConsole console,
        TerminalClientOptions options,
        IPrivateClientCredentialStore? credentialStore = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialStore = credentialStore ?? new ManualPrivateClientCredentialStore();
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var health = await _apiClient.CheckHealthAsync(cancellationToken);
        if (!health.IsSuccess)
        {
            WriteError(health.Error!);
            return 1;
        }

        var storedCredential = await _credentialStore.LoadAsync(cancellationToken);
        var credential = storedCredential ?? await GetCredentialAsync(cancellationToken);
        if (credential is null)
        {
            return 2;
        }

        var session = await _apiClient.CreateSessionAsync(credential.ClientId, credential.Credential, cancellationToken);
        if (!session.IsSuccess)
        {
            if (session.Error?.Code != "authentication_failed" || storedCredential is null)
            {
                WriteError(session.Error!);
                return 1;
            }

            _console.WriteLine("The stored private-client credential was rejected. Recover with pairing or a manual credential.");
            credential = await GetCredentialAsync(cancellationToken);
            if (credential is null)
            {
                return 2;
            }

            session = await _apiClient.CreateSessionAsync(credential.ClientId, credential.Credential, cancellationToken);
            if (!session.IsSuccess)
            {
                WriteError(session.Error!);
                return 1;
            }
        }

        await SaveCredentialAsync(credential, cancellationToken);
        return await ProcessMessagesAsync(credential, session.Value!.AccessToken, cancellationToken);
    }

    private async Task<PrivateClientCredential?> GetCredentialAsync(CancellationToken cancellationToken)
    {
        _console.Write("Private client ID (leave empty to pair): ");
        var clientId = _console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _console.Write("Administrative pairing challenge: ");
            var challenge = _console.ReadSecret();
            _console.Write("Private client display name: ");
            var displayName = _console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            var paired = await _apiClient.CompletePairingAsync(challenge, displayName, cancellationToken);
            if (!paired.IsSuccess)
            {
                WriteError(paired.Error!);
                return null;
            }

            return new PrivateClientCredential(paired.Value!.ClientId, paired.Value.Credential);
        }

        _console.Write("Private client credential: ");
        var value = _console.ReadSecret();
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(value)
            ? null
            : new PrivateClientCredential(clientId, value);
    }

    private async Task SaveCredentialAsync(PrivateClientCredential credential, CancellationToken cancellationToken)
    {
        if (!await _credentialStore.SaveAsync(credential, cancellationToken))
        {
            _console.WriteLine("The credential is valid for this session but could not be stored securely.");
        }
    }

    private async Task<int> ProcessMessagesAsync(
        PrivateClientCredential credential,
        string accessToken,
        CancellationToken cancellationToken)
    {
        Guid? conversationId = null;
        var provider = _options.Provider;
        var scenario = _options.Scenario;
        _console.WriteLine("LocalAssistant terminal client");
        _console.WriteLine($"Server: {_options.BaseUri}");
        _console.WriteLine(provider == "fake"
            ? $"Provider: fake (scenario: {scenario})"
            : "Provider: ollama (the server configures the model)");
        _console.WriteLine("Type /help for commands.");

        var resumed = await ResumeAsync(credential, accessToken, cancellationToken);
        credential = resumed.Credential;
        accessToken = resumed.AccessToken;
        conversationId = resumed.ConversationId;

        while (true)
        {
            _console.Write("You: ");
            var input = _console.ReadLine();
            if (input is null)
            {
                return 0;
            }

            input = input.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.StartsWith('/'))
            {
                var command = await HandleCommandAsync(
                    input, credential, accessToken, conversationId, provider, scenario, cancellationToken);
                accessToken = command.AccessToken;
                conversationId = command.ConversationId;
                provider = command.Provider;
                credential = command.Credential ?? credential;
                if (!command.Continue)
                {
                    return command.ExitCode;
                }

                continue;
            }

            var sent = await SendAsync(
                credential,
                accessToken,
                new SendMessageRequest(input, conversationId, provider, scenario),
                cancellationToken);
            accessToken = sent.AccessToken;
            if (!sent.Response.IsSuccess)
            {
                WriteError(sent.Response.Error!);
                continue;
            }

            conversationId = sent.Response.Value!.ConversationId;
            credential = await UpdateLastConversationAsync(
                credential,
                conversationId.Value,
                cancellationToken);
            ShowResponse(sent.Response.Value);
            if (sent.Response.Value.Confirmation is not null)
            {
                var resolved = await ResolveConfirmationAsync(
                    credential, accessToken, conversationId.Value, sent.Response.Value.Confirmation,
                    provider, scenario, cancellationToken);
                accessToken = resolved.AccessToken;
                if (resolved.Response.IsSuccess)
                {
                    conversationId = resolved.Response.Value!.ConversationId;
                    credential = await UpdateLastConversationAsync(
                        credential,
                        conversationId.Value,
                        cancellationToken);
                    ShowResponse(resolved.Response.Value);
                }
                else
                {
                    WriteError(resolved.Response.Error!);
                }
            }
        }
    }

    private async Task<(ClientResult<ConversationResponse> Response, string AccessToken)> SendAsync(
        PrivateClientCredential credential,
        string accessToken,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithRenewalAsync(
            credential,
            accessToken,
            token => _apiClient.SendMessageAsync(token, request, cancellationToken),
            cancellationToken);
    }

    private async Task<(PrivateClientCredential Credential, string AccessToken, Guid? ConversationId)> ResumeAsync(
        PrivateClientCredential credential,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (!credential.LastConversationId.HasValue)
        {
            return (credential, accessToken, null);
        }

        var details = await ExecuteWithRenewalAsync(
            credential,
            accessToken,
            token => _apiClient.GetConversationDetailsAsync(
                token,
                credential.LastConversationId.Value,
                cancellationToken),
            cancellationToken);
        accessToken = details.AccessToken;
        if (!details.Response.IsSuccess)
        {
            if (details.Response.Error?.Code == "not_found")
            {
                credential = await UpdateLastConversationAsync(credential, null, cancellationToken);
            }
            else if (details.Response.Error?.Code != "http_error")
            {
                _console.WriteLine("The previous conversation could not be checked. Starting a new conversation.");
            }

            return (credential, accessToken, null);
        }

        var conversation = details.Response.Value!;
        _console.WriteLine($"Last conversation: \"{conversation.Title}\" — {conversation.LastActivityAtUtc.LocalDateTime:g}");
        _console.Write("[R]esume  [N]ew  [L]ist conversations: ");
        var selection = _console.ReadLine()?.Trim();
        if (string.Equals(selection, "r", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selection, "resume", StringComparison.OrdinalIgnoreCase))
        {
            return (credential, accessToken, conversation.ConversationId);
        }

        if (string.Equals(selection, "l", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selection, "list", StringComparison.OrdinalIgnoreCase))
        {
            var listed = await SelectConversationAsync(
                credential,
                accessToken,
                null,
                cancellationToken);
            return (listed.Credential, listed.AccessToken, listed.ConversationId);
        }

        return (credential, accessToken, null);
    }

    private async Task<PrivateClientCredential> UpdateLastConversationAsync(
        PrivateClientCredential credential,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        var updated = credential with { LastConversationId = conversationId };
        if (!await _credentialStore.SaveAsync(updated, cancellationToken))
        {
            _console.WriteLine("The latest conversation could not be saved locally.");
            return credential;
        }

        return updated;
    }

    private async Task<(ClientResult<T> Response, string AccessToken)> ExecuteWithRenewalAsync<T>(
        PrivateClientCredential credential,
        string accessToken,
        Func<string, Task<ClientResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        var response = await operation(accessToken);
        if (!response.Error?.CanRenewSession ?? true)
        {
            return (response, accessToken);
        }

        var renewed = await _apiClient.CreateSessionAsync(credential.ClientId, credential.Credential, cancellationToken);
        if (!renewed.IsSuccess)
        {
            return (response, accessToken);
        }

        accessToken = renewed.Value!.AccessToken;
        return (await operation(accessToken), accessToken);
    }

    private async Task<(ClientResult<ConversationResponse> Response, string AccessToken)> ResolveConfirmationAsync(
        PrivateClientCredential credential,
        string accessToken,
        Guid conversationId,
        ToolConfirmationResponse confirmation,
        string provider,
        string scenario,
        CancellationToken cancellationToken)
    {
        _console.WriteLine($"Confirmation required for tool '{confirmation.ToolName}' before {confirmation.ExpiresAtUtc:O}.");
        while (true)
        {
            _console.Write("Type approve, reject, or cancel: ");
            var decision = _console.ReadLine()?.Trim();
            if (decision is null || decision.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                return (ClientResults.Failure<ConversationResponse>(
                    "confirmation_cancelled",
                    "The pending confirmation was not resolved."), accessToken);
            }

            if (decision.Equals("approve", StringComparison.OrdinalIgnoreCase) ||
                decision.Equals("reject", StringComparison.OrdinalIgnoreCase))
            {
                var approved = decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
                return await ExecuteWithRenewalAsync(
                    credential,
                    accessToken,
                    token => _apiClient.ResolveConfirmationAsync(
                        token,
                        conversationId,
                        confirmation.ConfirmationId,
                        new ResolveToolConfirmationRequest(approved, provider, scenario),
                        cancellationToken),
                    cancellationToken);
            }

            _console.WriteLine("Type approve, reject, or cancel.");
        }
    }

    private async Task<CommandResult> HandleCommandAsync(
        string input,
        PrivateClientCredential credential,
        string accessToken,
        Guid? conversationId,
        string provider,
        string scenario,
        CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts[0].Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            _console.WriteLine("Commands: /new, /conversations, /info, /provider fake|ollama, /admin rotate, /admin revoke, /exit");
            return new(true, 0, accessToken, provider, conversationId);
        }

        if (parts[0].Equals("/info", StringComparison.OrdinalIgnoreCase))
        {
            _console.WriteLine($"Server: {_options.BaseUri}; Provider: {provider}; Scenario: {scenario}; Conversation active: {conversationId.HasValue}.");
            return new(true, 0, accessToken, provider, conversationId);
        }

        if (parts[0].Equals("/conversations", StringComparison.OrdinalIgnoreCase))
        {
            var selection = await SelectConversationAsync(
                credential,
                accessToken,
                conversationId,
                cancellationToken);
            return new(true, 0, selection.AccessToken, provider, selection.ConversationId)
            {
                Credential = selection.Credential,
            };
        }

        if (parts.Length == 2 && parts[0].Equals("/admin", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleAdminAsync(parts[1], credential, accessToken, provider, conversationId, cancellationToken);
        }

        if (parts[0].Equals("/provider", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 2 || (parts[1] is not "fake" and not "ollama"))
            {
                _console.WriteLine("Usage: /provider fake or /provider ollama");
                return new(true, 0, accessToken, provider, conversationId);
            }

            var completion = await CompleteAsync(credential, accessToken, conversationId, cancellationToken);
            if (!completion.Completed)
            {
                return new(true, 0, completion.AccessToken, provider, conversationId);
            }

            return new(true, 0, completion.AccessToken, parts[1], null);
        }

        if (parts[0].Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            var completion = await CompleteAsync(credential, accessToken, conversationId, cancellationToken);
            return !completion.Completed
                ? new(true, 0, completion.AccessToken, provider, conversationId)
                : new(true, 0, completion.AccessToken, provider, null);
        }

        if (parts[0].Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            var completion = await CompleteAsync(credential, accessToken, conversationId, cancellationToken);
            return !completion.Completed
                ? new(true, 0, completion.AccessToken, provider, conversationId)
                : new(false, 0, completion.AccessToken, provider, conversationId);
        }

        _console.WriteLine("Unknown command. Type /help for available commands.");
        return new(true, 0, accessToken, provider, conversationId);
    }

    private async Task<(PrivateClientCredential Credential, string AccessToken, Guid? ConversationId)> SelectConversationAsync(
        PrivateClientCredential credential,
        string accessToken,
        Guid? currentConversationId,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        while (true)
        {
            var listed = await ExecuteWithRenewalAsync(
                credential,
                accessToken,
                token => _apiClient.ListConversationsAsync(token, cursor, 20, cancellationToken),
                cancellationToken);
            accessToken = listed.AccessToken;
            if (!listed.Response.IsSuccess)
            {
                if (listed.Response.Error?.Code == "persistence_unavailable")
                {
                    _console.WriteLine("Conversation persistence is unavailable. Starting a new conversation.");
                }
                else
                {
                    WriteError(listed.Response.Error!);
                }

                return (credential, accessToken, currentConversationId);
            }

            var page = listed.Response.Value!;
            for (var index = 0; index < page.Items.Count; index++)
            {
                var item = page.Items[index];
                _console.WriteLine($"{index + 1}. {item.Title} — {item.LastActivityAtUtc.LocalDateTime:g}");
            }

            _console.Write("Select a number, [N]ext, or [C]ancel: ");
            var selection = _console.ReadLine()?.Trim();
            if (string.Equals(selection, "n", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(page.NextCursor))
            {
                cursor = page.NextCursor;
                continue;
            }

            if (!int.TryParse(selection, out var selectedIndex) ||
                selectedIndex < 1 || selectedIndex > page.Items.Count)
            {
                return (credential, accessToken, currentConversationId);
            }

            var selected = page.Items[selectedIndex - 1];
            var details = await ExecuteWithRenewalAsync(
                credential,
                accessToken,
                token => _apiClient.GetConversationDetailsAsync(token, selected.ConversationId, cancellationToken),
                cancellationToken);
            accessToken = details.AccessToken;
            if (!details.Response.IsSuccess)
            {
                WriteError(details.Response.Error!);
                return (credential, accessToken, currentConversationId);
            }

            var history = await ExecuteWithRenewalAsync(
                credential,
                accessToken,
                token => _apiClient.GetConversationHistoryAsync(token, selected.ConversationId, null, 20, cancellationToken),
                cancellationToken);
            accessToken = history.AccessToken;
            if (!history.Response.IsSuccess)
            {
                WriteError(history.Response.Error!);
                return (credential, accessToken, currentConversationId);
            }

            foreach (var message in history.Response.Value!.Items)
            {
                _console.WriteLine($"{message.Role}: {message.Content}");
            }

            if (currentConversationId.HasValue && currentConversationId != selected.ConversationId)
            {
                var completion = await CompleteAsync(
                    credential,
                    accessToken,
                    currentConversationId,
                    cancellationToken);
                accessToken = completion.AccessToken;
                if (!completion.Completed)
                {
                    return (credential, accessToken, currentConversationId);
                }
            }

            credential = await UpdateLastConversationAsync(
                credential,
                details.Response.Value!.ConversationId,
                cancellationToken);
            return (credential, accessToken, details.Response.Value.ConversationId);
        }
    }

    private async Task<CommandResult> HandleAdminAsync(
        string operation,
        PrivateClientCredential credential,
        string accessToken,
        string provider,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (operation.Equals("rotate", StringComparison.OrdinalIgnoreCase))
        {
            _console.Write("Administrative rotation challenge: ");
            var challenge = _console.ReadSecret();
            var rotation = await _apiClient.RotateCredentialAsync(challenge, credential.ClientId, cancellationToken);
            if (!rotation.IsSuccess || !string.Equals(rotation.Value!.ClientId, credential.ClientId, StringComparison.Ordinal))
            {
                WriteError(rotation.Error ?? new ClientError("invalid_response", "Credential rotation was rejected."));
                return new(true, 0, accessToken, provider, conversationId);
            }

            var replacement = new PrivateClientCredential(
                credential.ClientId,
                rotation.Value.Credential,
                credential.LastConversationId);
            var session = await _apiClient.CreateSessionAsync(replacement.ClientId, replacement.Credential, cancellationToken);
            if (!session.IsSuccess)
            {
                WriteError(session.Error!);
                return new(true, 0, accessToken, provider, conversationId);
            }

            if (!await _credentialStore.SaveAsync(replacement, cancellationToken))
            {
                _console.WriteLine("The credential was rotated but could not be stored. Pair again after this session ends.");
            }

            return new(true, 0, session.Value!.AccessToken, provider, conversationId)
            {
                Credential = replacement,
            };
        }

        if (operation.Equals("revoke", StringComparison.OrdinalIgnoreCase))
        {
            _console.Write("Type REVOKE to revoke this client: ");
            if (!string.Equals(_console.ReadLine(), "REVOKE", StringComparison.Ordinal))
            {
                return new(true, 0, accessToken, provider, conversationId);
            }

            _console.Write("Administrative revocation challenge: ");
            var challenge = _console.ReadSecret();
            var revoked = await _apiClient.RevokeClientAsync(challenge, credential.ClientId, cancellationToken);
            if (!revoked.IsSuccess || !string.Equals(revoked.Value!.ClientId, credential.ClientId, StringComparison.Ordinal))
            {
                WriteError(revoked.Error ?? new ClientError("invalid_response", "Client revocation was rejected."));
                return new(true, 0, accessToken, provider, conversationId);
            }

            if (!await _credentialStore.DeleteAsync(cancellationToken))
            {
                _console.WriteLine("The client was revoked, but its local credential state could not be removed.");
            }

            return new(false, 0, string.Empty, provider, null);
        }

        _console.WriteLine("Usage: /admin rotate or /admin revoke");
        return new(true, 0, accessToken, provider, conversationId);
    }

    private async Task<(bool Completed, string AccessToken)> CompleteAsync(
        PrivateClientCredential credential,
        string accessToken,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (!conversationId.HasValue)
        {
            return (true, accessToken);
        }

        var completed = await ExecuteWithRenewalAsync(
            credential,
            accessToken,
            token => _apiClient.CompleteConversationAsync(token, conversationId.Value, cancellationToken),
            cancellationToken);
        if (!completed.Response.IsSuccess)
        {
            WriteError(completed.Response.Error!);
            return (false, completed.AccessToken);
        }

        return (true, completed.AccessToken);
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
            _console.WriteLine($"Tool: {tool.ToolName} ({(tool.Succeeded ? "completed" : "failed")})");
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

    private sealed record CommandResult(
        bool Continue,
        int ExitCode,
        string AccessToken,
        string Provider,
        Guid? ConversationId)
    {
        public PrivateClientCredential? Credential { get; init; }
    }
}
