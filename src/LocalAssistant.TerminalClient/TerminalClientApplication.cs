namespace LocalAssistant.TerminalClient;

public sealed class TerminalClientApplication
{
    private readonly PrivateApiClient _apiClient;
    private readonly ITerminalConsole _console;
    private readonly TerminalClientOptions _options;
    private readonly IPrivateClientCredentialStore _credentialStore;
    private readonly TerminalClientStateCoordinator _stateCoordinator;

    public TerminalClientApplication(
        PrivateApiClient apiClient,
        ITerminalConsole console,
        TerminalClientOptions options,
        IPrivateClientCredentialStore? credentialStore = null)
        : this(
            apiClient,
            console,
            options,
            credentialStore ?? new ManualPrivateClientCredentialStore(),
            new TerminalClientStateTextSink(console))
    {
    }

    internal TerminalClientApplication(
        PrivateApiClient apiClient,
        ITerminalConsole console,
        TerminalClientOptions options,
        IPrivateClientCredentialStore credentialStore,
        ITerminalClientStateSink stateSink)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _stateCoordinator = new TerminalClientStateCoordinator(stateSink);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _stateCoordinator.PublishInitial();
        try
        {
            MoveTo(TerminalClientLifecycle.Connecting, TerminalClientActivity.None, error: null);
            var health = await _apiClient.CheckHealthAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!health.IsSuccess)
            {
                Block(health.Error!, "health");
                WriteError(health.Error!);
                return 1;
            }

            MoveTo(TerminalClientLifecycle.Authenticating, TerminalClientActivity.None, error: null);
            var storedCredential = await _credentialStore.LoadAsync(cancellationToken);
            var credential = storedCredential ?? await GetCredentialAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (credential is null)
            {
                Block(new ClientError("authentication_cancelled", "Authentication was not completed."), "authentication");
                return 2;
            }

            var session = await _apiClient.CreateSessionAsync(credential.ClientId, credential.Credential, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsSuccess)
            {
                if (session.Error?.Code != "authentication_failed" || storedCredential is null)
                {
                    Block(session.Error!, "authentication");
                    WriteError(session.Error!);
                    return 1;
                }

                _console.WriteLine("The stored private-client credential was rejected. Recover with pairing or a manual credential.");
                credential = await GetCredentialAsync(cancellationToken);
                if (credential is null)
                {
                    Block(new ClientError("authentication_cancelled", "Authentication was not completed."), "authentication");
                    return 2;
                }

                session = await _apiClient.CreateSessionAsync(credential.ClientId, credential.Credential, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!session.IsSuccess)
                {
                    Block(session.Error!, "authentication");
                    WriteError(session.Error!);
                    return 1;
                }
            }

            Ready(_options.Provider, conversationId: null, pendingConfirmation: null, clearError: true);
            await SaveCredentialAsync(credential, cancellationToken);
            return await ProcessMessagesAsync(credential, session.Value!.AccessToken, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HandleCancellation();
            _console.WriteLine("The client operation was cancelled.");
            return 2;
        }
        catch (Exception)
        {
            Block(new ClientError("client_error", "The client could not continue."), "client");
            _console.WriteLine("The client could not continue.");
            return 1;
        }
        finally
        {
            Close();
        }
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
            cancellationToken.ThrowIfCancellationRequested();
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
            RecordError(
                new ClientError("credential_not_saved", "The credential could not be stored securely."),
                "credential",
                canBeUncertain: false);
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
        cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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

            BeginActivity(TerminalClientActivity.SendingTurn);
            var sent = await SendAsync(
                credential,
                accessToken,
                new SendMessageRequest(input, conversationId, provider, scenario),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            accessToken = sent.AccessToken;
            if (!sent.Response.IsSuccess)
            {
                RecordError(sent.Response.Error!, "turn", canBeUncertain: true);
                WriteError(sent.Response.Error!);
                continue;
            }

            conversationId = sent.Response.Value!.ConversationId;
            var currentState = _stateCoordinator.Current;
            MoveTo(
                TerminalClientLifecycle.Ready,
                TerminalClientActivity.SendingTurn,
                currentState.Error,
                provider,
                conversationId,
                pendingConfirmation: null,
                replaceConversation: true);
            credential = await UpdateLastConversationAsync(
                credential,
                conversationId.Value,
                cancellationToken);
            ShowResponse(sent.Response.Value);
            if (sent.Response.Value.Confirmation is not null)
            {
                var confirmation = sent.Response.Value.Confirmation;
                MoveTo(
                    TerminalClientLifecycle.Ready,
                    TerminalClientActivity.AwaitingConfirmation,
                    ToConversationError(sent.Response.Value.Error, "turn"),
                    provider,
                    conversationId,
                    new TerminalClientPendingConfirmation(
                        confirmation.ToolName,
                        confirmation.ExpiresAtUtc),
                    replaceConversation: true);
                var resolved = await ResolveConfirmationAsync(
                    credential, accessToken, conversationId.Value, confirmation,
                    provider, scenario, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                accessToken = resolved.AccessToken;
                if (resolved.Response.IsSuccess)
                {
                    conversationId = resolved.Response.Value!.ConversationId;
                    credential = await UpdateLastConversationAsync(
                        credential,
                        conversationId.Value,
                        cancellationToken);
                    ShowResponse(resolved.Response.Value);
                    if (resolved.Response.Value.Confirmation is not null)
                    {
                        var nextConfirmation = resolved.Response.Value.Confirmation;
                        MoveTo(
                            TerminalClientLifecycle.Ready,
                            TerminalClientActivity.AwaitingConfirmation,
                            ToConversationError(resolved.Response.Value.Error, "confirmation"),
                            provider,
                            conversationId,
                            new TerminalClientPendingConfirmation(
                                nextConfirmation.ToolName,
                                nextConfirmation.ExpiresAtUtc),
                            replaceConversation: true);
                    }
                    else
                    {
                        if (resolved.Response.Value.Error is not null)
                        {
                            RecordConversationError(resolved.Response.Value.Error, "confirmation");
                        }
                        else
                        {
                            Ready(provider, conversationId, pendingConfirmation: null, clearError: true);
                        }
                    }
                }
                else
                {
                    RecordError(resolved.Response.Error!, "confirmation", canBeUncertain: true);
                    WriteError(resolved.Response.Error!);
                }
            }
            else
            {
                if (sent.Response.Value.Error is not null)
                {
                    RecordConversationError(sent.Response.Value.Error, "turn");
                }
                else
                {
                    Ready(provider, conversationId, pendingConfirmation: null, clearError: true);
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
        BeginActivity(TerminalClientActivity.ResumingConversation);
        if (!credential.LastConversationId.HasValue)
        {
            Ready(_options.Provider, conversationId: null, pendingConfirmation: null, clearError: true);
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
                Ready(_options.Provider, conversationId: null, pendingConfirmation: null, clearError: true);
            }
            else
            {
                _console.WriteLine("The previous conversation could not be checked. Starting a new conversation.");
                RecordError(details.Response.Error!, "resume", canBeUncertain: false);
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
            var history = await ShowHistoryAsync(
                credential,
                accessToken,
                conversation.ConversationId,
                cancellationToken);
            if (history.Loaded)
            {
                Ready(
                    _options.Provider,
                    conversation.ConversationId,
                    pendingConfirmation: null,
                    clearError: true);
                return (credential, history.AccessToken, conversation.ConversationId);
            }

            if (history.Error?.Code == "not_found")
            {
                credential = await UpdateLastConversationAsync(credential, null, cancellationToken);
                Ready(_options.Provider, conversationId: null, pendingConfirmation: null, clearError: true);
            }
            else if (history.Error is not null)
            {
                RecordError(history.Error, "resume", canBeUncertain: false);
            }

            return (credential, history.AccessToken, null);
        }

        if (string.Equals(selection, "l", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selection, "list", StringComparison.OrdinalIgnoreCase))
        {
            Ready(_options.Provider, conversationId: null, pendingConfirmation: null, clearError: true);
            var listed = await SelectConversationAsync(
                credential,
                accessToken,
                null,
                cancellationToken);
            return (listed.Credential, listed.AccessToken, listed.ConversationId);
        }

        credential = await UpdateLastConversationAsync(credential, null, cancellationToken);
        Ready(_options.Provider, conversationId: null, pendingConfirmation: null, clearError: true);
        return (credential, accessToken, null);
    }

    private async Task<PrivateClientCredential> UpdateLastConversationAsync(
        PrivateClientCredential credential,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (credential.LastConversationId == conversationId)
        {
            return credential;
        }

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
                BeginActivity(TerminalClientActivity.ResolvingConfirmation);
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

            var updatedCredential = await UpdateLastConversationAsync(credential, null, cancellationToken);
            Ready(parts[1], conversationId: null, pendingConfirmation: null, clearError: true);
            return new(true, 0, completion.AccessToken, parts[1], null)
            {
                Credential = updatedCredential,
            };
        }

        if (parts[0].Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            var completion = await CompleteAsync(credential, accessToken, conversationId, cancellationToken);
            if (!completion.Completed)
            {
                return new(true, 0, completion.AccessToken, provider, conversationId);
            }

            var updatedCredential = await UpdateLastConversationAsync(credential, null, cancellationToken);
            Ready(provider, conversationId: null, pendingConfirmation: null, clearError: true);
            return new(true, 0, completion.AccessToken, provider, null)
            {
                Credential = updatedCredential,
            };
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
        BeginActivity(TerminalClientActivity.SelectingConversation);
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

                RecordError(listed.Response.Error!, "selection", canBeUncertain: false);
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
                Ready(_options.Provider, currentConversationId, pendingConfirmation: null, clearError: true);
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
                RecordError(details.Response.Error!, "selection", canBeUncertain: false);
                WriteError(details.Response.Error!);
                return (credential, accessToken, currentConversationId);
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

                BeginActivity(TerminalClientActivity.SelectingConversation);
            }

            var history = await ShowHistoryAsync(
                credential,
                accessToken,
                selected.ConversationId,
                cancellationToken);
            accessToken = history.AccessToken;
            if (!history.Loaded)
            {
                if (history.Error is not null)
                {
                    RecordError(history.Error, "selection", canBeUncertain: false);
                }

                return (credential, accessToken, currentConversationId);
            }

            credential = await UpdateLastConversationAsync(
                credential,
                details.Response.Value!.ConversationId,
                cancellationToken);
            Ready(
                _options.Provider,
                details.Response.Value.ConversationId,
                pendingConfirmation: null,
                clearError: true);
            return (credential, accessToken, details.Response.Value.ConversationId);
        }
    }

    private async Task<(bool Loaded, string AccessToken, ClientError? Error)> ShowHistoryAsync(
        PrivateClientCredential credential,
        string accessToken,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        while (true)
        {
            var history = await ExecuteWithRenewalAsync(
                credential,
                accessToken,
                token => _apiClient.GetConversationHistoryAsync(
                    token,
                    conversationId,
                    cursor,
                    20,
                    cancellationToken),
                cancellationToken);
            accessToken = history.AccessToken;
            if (!history.Response.IsSuccess)
            {
                WriteError(history.Response.Error!);
                return (false, accessToken, history.Response.Error);
            }

            foreach (var message in history.Response.Value!.Items)
            {
                _console.WriteLine($"{message.Role}: {message.Content}");
            }

            if (string.IsNullOrWhiteSpace(history.Response.Value.NextCursor))
            {
                return (true, accessToken, null);
            }

            _console.Write("[N]ext history page or [C]ontinue: ");
            if (!string.Equals(_console.ReadLine()?.Trim(), "n", StringComparison.OrdinalIgnoreCase))
            {
                return (true, accessToken, null);
            }

            cursor = history.Response.Value.NextCursor;
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

        BeginActivity(TerminalClientActivity.CompletingConversation);
        var completed = await ExecuteWithRenewalAsync(
            credential,
            accessToken,
            token => _apiClient.CompleteConversationAsync(token, conversationId.Value, cancellationToken),
            cancellationToken);
        if (!completed.Response.IsSuccess)
        {
            RecordError(completed.Response.Error!, "completion", canBeUncertain: true);
            WriteError(completed.Response.Error!);
            return (false, completed.AccessToken);
        }

        var current = _stateCoordinator.Current;
        Ready(current.Provider, current.ConversationId, pendingConfirmation: null, clearError: true);
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

    private void BeginActivity(TerminalClientActivity activity)
    {
        var current = _stateCoordinator.Current;
        MoveTo(
            TerminalClientLifecycle.Ready,
            activity,
            current.Error,
            current.Provider,
            current.ConversationId,
            current.PendingConfirmation);
    }

    private void Ready(
        string? provider,
        Guid? conversationId,
        TerminalClientPendingConfirmation? pendingConfirmation,
        bool clearError)
    {
        var current = _stateCoordinator.Current;
        MoveTo(
            TerminalClientLifecycle.Ready,
            TerminalClientActivity.None,
            clearError ? null : current.Error,
            provider,
            conversationId,
            pendingConfirmation,
            replaceConversation: true);
    }

    private void RecordError(ClientError error, string operation, bool canBeUncertain)
    {
        var current = _stateCoordinator.Current;
        var category = canBeUncertain && error.IsUncertain
            ? TerminalClientErrorCategory.Uncertain
            : TerminalClientErrorCategory.Recoverable;
        MoveTo(
            TerminalClientLifecycle.Ready,
            TerminalClientActivity.None,
            new TerminalClientOperationError(category, error.Code, error.Message, operation),
            current.Provider,
            current.ConversationId,
            pendingConfirmation: null);
    }

    private void RecordConversationError(ConversationErrorResponse error, string operation)
    {
        RecordError(
            new ClientError(error.Code, error.Message),
            operation,
            canBeUncertain: false);
    }

    private static TerminalClientOperationError? ToConversationError(
        ConversationErrorResponse? error,
        string operation) => error is null
        ? null
        : new TerminalClientOperationError(
            TerminalClientErrorCategory.Recoverable,
            error.Code,
            error.Message,
            operation);

    private void Block(ClientError error, string operation)
    {
        var current = _stateCoordinator.Current;
        MoveTo(
            TerminalClientLifecycle.Blocked,
            TerminalClientActivity.None,
            new TerminalClientOperationError(
                TerminalClientErrorCategory.Blocking,
                error.Code,
                error.Message,
                operation),
            current.Provider,
            current.ConversationId,
            pendingConfirmation: null);
    }

    private void HandleCancellation()
    {
        var current = _stateCoordinator.Current;
        if (current.Lifecycle is TerminalClientLifecycle.Connecting or TerminalClientLifecycle.Authenticating)
        {
            Block(new ClientError("operation_cancelled", "The operation was cancelled."), "authentication");
            return;
        }

        if (current.Lifecycle != TerminalClientLifecycle.Ready)
        {
            return;
        }

        var canBeUncertain = current.Activity is
            TerminalClientActivity.SendingTurn or
            TerminalClientActivity.ResolvingConfirmation or
            TerminalClientActivity.CompletingConversation;
        RecordError(
            new ClientError("operation_cancelled", "The operation was cancelled.", canBeUncertain),
            GetOperation(current.Activity),
            canBeUncertain);
    }

    private void Close()
    {
        var current = _stateCoordinator.Current;
        if (current.Lifecycle == TerminalClientLifecycle.Closed)
        {
            return;
        }

        MoveTo(
            TerminalClientLifecycle.Closing,
            TerminalClientActivity.None,
            current.Error,
            current.Provider,
            current.ConversationId,
            pendingConfirmation: null);
        current = _stateCoordinator.Current;
        MoveTo(
            TerminalClientLifecycle.Closed,
            TerminalClientActivity.None,
            current.Error,
            current.Provider,
            current.ConversationId,
            pendingConfirmation: null);
    }

    private void MoveTo(
        TerminalClientLifecycle lifecycle,
        TerminalClientActivity activity,
        TerminalClientOperationError? error,
        string? provider = null,
        Guid? conversationId = null,
        TerminalClientPendingConfirmation? pendingConfirmation = null,
        bool replaceConversation = false)
    {
        var current = _stateCoordinator.Current;
        var next = new TerminalClientStateSnapshot(
            lifecycle,
            activity,
            error,
            provider ?? current.Provider,
            replaceConversation ? conversationId : conversationId ?? current.ConversationId,
            pendingConfirmation);
        _stateCoordinator.TryTransition(next);
    }

    private static string GetOperation(TerminalClientActivity activity) => activity switch
    {
        TerminalClientActivity.SendingTurn => "turn",
        TerminalClientActivity.ResolvingConfirmation => "confirmation",
        TerminalClientActivity.CompletingConversation => "completion",
        TerminalClientActivity.ResumingConversation => "resume",
        TerminalClientActivity.SelectingConversation => "selection",
        _ => "client",
    };

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
