namespace LocalAssistant.TerminalClient;

public sealed class TerminalClientApplication
{
    private readonly PrivateApiClient _apiClient;
    private readonly ITerminalConsole _console;
    private readonly TerminalClientOptions _options;
    private readonly IPrivateClientCredentialStore _credentialStore;
    private readonly ISpokenOutputCoordinator _spokenOutput;
    private readonly TerminalClientStateCoordinator _stateCoordinator;
    private bool _spokenOutputCancellationRecorded;

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
            new TerminalClientStateTextSink(console),
            new UnavailableSpokenOutputCoordinator())
    {
    }

    internal TerminalClientApplication(
        PrivateApiClient apiClient,
        ITerminalConsole console,
        TerminalClientOptions options,
        IPrivateClientCredentialStore credentialStore,
        ITerminalClientStateSink stateSink,
        ISpokenOutputCoordinator? spokenOutput = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _spokenOutput = spokenOutput ?? new UnavailableSpokenOutputCoordinator();
        _stateCoordinator = new TerminalClientStateCoordinator(stateSink, _spokenOutput.State);
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
            var acquisition = storedCredential is null
                ? await GetCredentialAsync(cancellationToken)
                : CredentialAcquisitionResult.Obtained(storedCredential);
            var credential = acquisition.Credential;
            if (credential is null)
            {
                if (acquisition.Error is not null)
                {
                    Block(acquisition.Error, "pairing");
                    WriteError(acquisition.Error);
                    return 1;
                }

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
                acquisition = await GetCredentialAsync(cancellationToken);
                credential = acquisition.Credential;
                if (credential is null)
                {
                    if (acquisition.Error is not null)
                    {
                        Block(acquisition.Error, "pairing");
                        WriteError(acquisition.Error);
                        return 1;
                    }

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
            try
            {
                await _spokenOutput.DisposeAsync();
            }
            catch (Exception)
            {
                // Cleanup is local and must not prevent the guaranteed Closing -> Closed
                // transition. Prepared-output failures have already been reported at the
                // operation boundary when the application is still able to continue.
            }
            finally
            {
                Close();
            }
        }
    }

    private async Task<CredentialAcquisitionResult> GetCredentialAsync(CancellationToken cancellationToken)
    {
        var clientId = ReadLine(new TerminalInputRequest(
            TerminalInputKind.Line,
            "Private client ID (leave empty to pair): "))?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            var challenge = ReadSecret(new TerminalInputRequest(
                TerminalInputKind.Secret,
                "Administrative pairing challenge: "));
            var displayName = ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Private client display name: "))?.Trim();
            if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(displayName))
            {
                return CredentialAcquisitionResult.Cancelled;
            }

            var paired = await _apiClient.CompletePairingAsync(challenge, displayName, cancellationToken);
            if (!paired.IsSuccess)
            {
                return CredentialAcquisitionResult.Failed(paired.Error!);
            }

            return CredentialAcquisitionResult.Obtained(
                new PrivateClientCredential(paired.Value!.ClientId, paired.Value.Credential));
        }

        var value = ReadSecret(new TerminalInputRequest(
            TerminalInputKind.Secret,
            "Private client credential: "));
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(value)
            ? CredentialAcquisitionResult.Cancelled
            : CredentialAcquisitionResult.Obtained(new PrivateClientCredential(clientId, value));
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

        var resumed = await ResumeAsync(credential, accessToken, provider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        credential = resumed.Credential;
        accessToken = resumed.AccessToken;
        conversationId = resumed.ConversationId;

        while (true)
        {
            var input = ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "You: "));
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
            var persistedConversation = await UpdateLastConversationAsync(
                credential,
                conversationId.Value,
                cancellationToken);
            credential = persistedConversation.Credential;
            ShowResponse(sent.Response.Value);
            if (sent.Response.Value.Confirmation is not null)
            {
                var confirmation = sent.Response.Value.Confirmation;
                MoveTo(
                    TerminalClientLifecycle.Ready,
                    TerminalClientActivity.AwaitingConfirmation,
                    ToOperationError(persistedConversation.Error, "conversation_preference") ??
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
                    var persistedResolvedConversation = await UpdateLastConversationAsync(
                        credential,
                        conversationId.Value,
                        cancellationToken);
                    credential = persistedResolvedConversation.Credential;
                    ShowResponse(resolved.Response.Value);
                    var spokenOutputError = await PlaySpokenOutputAsync(
                        resolved.Response.Value,
                        cancellationToken);
                    if (resolved.Response.Value.Confirmation is not null)
                    {
                        var nextConfirmation = resolved.Response.Value.Confirmation;
                        MoveTo(
                            TerminalClientLifecycle.Ready,
                            TerminalClientActivity.AwaitingConfirmation,
                            ToOperationError(persistedResolvedConversation.Error, "conversation_preference") ??
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
                        if (spokenOutputError is not null)
                        {
                            Ready(
                                provider,
                                conversationId,
                                pendingConfirmation: null,
                                clearError: false,
                                errorOverride: ToOperationError(spokenOutputError, "speech_output"));
                            WriteError(spokenOutputError);
                        }
                        else if (persistedResolvedConversation.Error is not null)
                        {
                            Ready(
                                provider,
                                conversationId,
                                pendingConfirmation: null,
                                clearError: false,
                                errorOverride: ToOperationError(
                                    persistedResolvedConversation.Error,
                                    "conversation_preference"));
                        }
                        else if (resolved.Response.Value.Error is not null)
                        {
                            RecordConversationError(resolved.Response.Value.Error, "confirmation");
                        }
                        else
                        {
                            Ready(
                                provider,
                                conversationId,
                                pendingConfirmation: null,
                                clearError: persistedResolvedConversation.Error is null,
                                errorOverride: ToOperationError(
                                    persistedResolvedConversation.Error,
                                    "conversation_preference"));
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
                var spokenOutputError = await PlaySpokenOutputAsync(
                    sent.Response.Value,
                    cancellationToken);
                if (spokenOutputError is not null)
                {
                    Ready(
                        provider,
                        conversationId,
                        pendingConfirmation: null,
                        clearError: false,
                        errorOverride: ToOperationError(spokenOutputError, "speech_output"));
                    WriteError(spokenOutputError);
                }
                else if (persistedConversation.Error is not null)
                {
                    Ready(
                        provider,
                        conversationId,
                        pendingConfirmation: null,
                        clearError: false,
                        errorOverride: ToOperationError(
                            persistedConversation.Error,
                            "conversation_preference"));
                }
                else if (sent.Response.Value.Error is not null)
                {
                    RecordConversationError(sent.Response.Value.Error, "turn");
                }
                else
                {
                    Ready(
                        provider,
                        conversationId,
                        pendingConfirmation: null,
                        clearError: persistedConversation.Error is null,
                        errorOverride: ToOperationError(
                            persistedConversation.Error,
                            "conversation_preference"));
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
        string provider,
        CancellationToken cancellationToken)
    {
        BeginActivity(TerminalClientActivity.ResumingConversation);
        if (!credential.LastConversationId.HasValue)
        {
            Ready(provider, conversationId: null, pendingConfirmation: null, clearError: true);
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
                var clearedMissingConversation = await UpdateLastConversationAsync(credential, null, cancellationToken);
                credential = clearedMissingConversation.Credential;
                Ready(
                    provider,
                    conversationId: null,
                    pendingConfirmation: null,
                    clearError: clearedMissingConversation.Error is null,
                    errorOverride: ToOperationError(clearedMissingConversation.Error, "conversation_preference"));
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
        var selection = ReadLine(new TerminalInputRequest(
            TerminalInputKind.Line,
            "[R]esume  [N]ew  [L]ist conversations: "))?.Trim();
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
                    provider,
                    conversation.ConversationId,
                    pendingConfirmation: null,
                    clearError: true);
                return (credential, history.AccessToken, conversation.ConversationId);
            }

            if (history.Error?.Code == "not_found")
            {
                var clearedMissingHistory = await UpdateLastConversationAsync(credential, null, cancellationToken);
                credential = clearedMissingHistory.Credential;
                Ready(
                    provider,
                    conversationId: null,
                    pendingConfirmation: null,
                    clearError: clearedMissingHistory.Error is null,
                    errorOverride: ToOperationError(clearedMissingHistory.Error, "conversation_preference"));
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
            Ready(provider, conversationId: null, pendingConfirmation: null, clearError: true);
            var listed = await SelectConversationAsync(
                credential,
                accessToken,
                null,
                provider,
                cancellationToken);
            return (listed.Credential, listed.AccessToken, listed.ConversationId);
        }

        var clearedConversation = await UpdateLastConversationAsync(credential, null, cancellationToken);
        credential = clearedConversation.Credential;
        Ready(
            provider,
            conversationId: null,
            pendingConfirmation: null,
            clearError: clearedConversation.Error is null,
            errorOverride: ToOperationError(clearedConversation.Error, "conversation_preference"));
        return (credential, accessToken, null);
    }

    private async Task<LastConversationUpdateResult> UpdateLastConversationAsync(
        PrivateClientCredential credential,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (credential.LastConversationId == conversationId)
        {
            return new LastConversationUpdateResult(credential, null);
        }

        var updated = credential with { LastConversationId = conversationId };
        if (!await _credentialStore.SaveAsync(updated, cancellationToken))
        {
            _console.WriteLine("The latest conversation could not be saved locally.");
            return new LastConversationUpdateResult(
                credential,
                new ClientError("last_conversation_not_saved", "The latest conversation could not be saved locally."));
        }

        return new LastConversationUpdateResult(updated, null);
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
            var decision = ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Type approve, reject, or cancel: "))?.Trim();
            if (decision is null)
            {
                // The input channel closed (EOF / Ctrl+C): the client is shutting down.
                // Do not issue an HTTP call during teardown; the server-side confirmation
                // is left to expire on its own.
                return (ClientResults.Failure<ConversationResponse>(
                    "confirmation_cancelled",
                    "The pending confirmation was not resolved."), accessToken);
            }

            if (decision.Equals("approve", StringComparison.OrdinalIgnoreCase) ||
                decision.Equals("reject", StringComparison.OrdinalIgnoreCase) ||
                decision.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                // 'cancel' resolves the pending confirmation as a rejection so the
                // conversation is not left wedged on the server for the next turn.
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
                provider,
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
            Ready(
                parts[1],
                conversationId: null,
                pendingConfirmation: null,
                clearError: updatedCredential.Error is null,
                errorOverride: ToOperationError(updatedCredential.Error, "conversation_preference"));
            return new(true, 0, completion.AccessToken, parts[1], null)
            {
                Credential = updatedCredential.Credential,
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
            Ready(
                provider,
                conversationId: null,
                pendingConfirmation: null,
                clearError: updatedCredential.Error is null,
                errorOverride: ToOperationError(updatedCredential.Error, "conversation_preference"));
            return new(true, 0, completion.AccessToken, provider, null)
            {
                Credential = updatedCredential.Credential,
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
        string provider,
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

            var selection = ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Select a number, [N]ext, or [C]ancel: "))?.Trim();
            if (string.Equals(selection, "n", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(page.NextCursor))
            {
                cursor = page.NextCursor;
                continue;
            }

            if (!int.TryParse(selection, out var selectedIndex) ||
                selectedIndex < 1 || selectedIndex > page.Items.Count)
            {
                Ready(provider, currentConversationId, pendingConfirmation: null, clearError: true);
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

            var persistedSelection = await UpdateLastConversationAsync(
                credential,
                details.Response.Value!.ConversationId,
                cancellationToken);
            credential = persistedSelection.Credential;
            Ready(
                provider,
                details.Response.Value.ConversationId,
                pendingConfirmation: null,
                clearError: persistedSelection.Error is null,
                errorOverride: ToOperationError(persistedSelection.Error, "conversation_preference"));
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
                WriteConversationMessage(message.Role, message.Content);
            }

            if (string.IsNullOrWhiteSpace(history.Response.Value.NextCursor))
            {
                return (true, accessToken, null);
            }

            var nextPage = ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "[N]ext history page or [C]ontinue: "))?.Trim();
            if (!string.Equals(nextPage, "n", StringComparison.OrdinalIgnoreCase))
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
            var challenge = ReadSecret(new TerminalInputRequest(
                TerminalInputKind.Secret,
                "Administrative rotation challenge: "));
            var rotation = await _apiClient.RotateCredentialAsync(challenge, credential.ClientId, cancellationToken);
            if (!rotation.IsSuccess || !string.Equals(rotation.Value!.ClientId, credential.ClientId, StringComparison.Ordinal))
            {
                var error = rotation.Error ?? new ClientError("invalid_response", "Credential rotation was rejected.");
                if (error.IsUncertain)
                {
                    Block(error, "credential_rotation");
                    WriteError(error);
                    return new(false, 1, accessToken, provider, conversationId);
                }

                RecordError(error, "credential_rotation", canBeUncertain: true);
                WriteError(error);
                return new(true, 0, accessToken, provider, conversationId);
            }

            var replacement = new PrivateClientCredential(
                credential.ClientId,
                rotation.Value.Credential,
                credential.LastConversationId);
            var session = await _apiClient.CreateSessionAsync(replacement.ClientId, replacement.Credential, cancellationToken);
            if (!session.IsSuccess)
            {
                Block(session.Error!, "credential_rotation");
                WriteError(session.Error!);
                return new(false, 1, accessToken, provider, conversationId);
            }

            if (!await _credentialStore.SaveAsync(replacement, cancellationToken))
            {
                _console.WriteLine("The credential was rotated but could not be stored. Pair again after this session ends.");
                Block(
                    new ClientError("rotated_credential_not_saved", "The rotated credential could not be stored securely."),
                    "credential_rotation");
                return new(false, 1, session.Value!.AccessToken, provider, conversationId);
            }

            return new(true, 0, session.Value!.AccessToken, provider, conversationId)
            {
                Credential = replacement,
            };
        }

        if (operation.Equals("revoke", StringComparison.OrdinalIgnoreCase))
        {
            var revocationConfirmation = ReadLine(new TerminalInputRequest(
                TerminalInputKind.Line,
                "Type REVOKE to revoke this client: "));
            if (!string.Equals(revocationConfirmation, "REVOKE", StringComparison.Ordinal))
            {
                return new(true, 0, accessToken, provider, conversationId);
            }

            var challenge = ReadSecret(new TerminalInputRequest(
                TerminalInputKind.Secret,
                "Administrative revocation challenge: "));
            var revoked = await _apiClient.RevokeClientAsync(challenge, credential.ClientId, cancellationToken);
            if (!revoked.IsSuccess || !string.Equals(revoked.Value!.ClientId, credential.ClientId, StringComparison.Ordinal))
            {
                var error = revoked.Error ?? new ClientError("invalid_response", "Client revocation was rejected.");
                if (error.IsUncertain)
                {
                    Block(error, "credential_revocation");
                    WriteError(error);
                    return new(false, 1, accessToken, provider, conversationId);
                }

                RecordError(error, "credential_revocation", canBeUncertain: true);
                WriteError(error);
                return new(true, 0, accessToken, provider, conversationId);
            }

            if (!await _credentialStore.DeleteAsync(cancellationToken))
            {
                _console.WriteLine("The client was revoked, but its local credential state could not be removed.");
                Block(
                    new ClientError("revoked_credential_not_deleted", "The revoked credential could not be removed locally."),
                    "credential_revocation");
                return new(false, 1, string.Empty, provider, null);
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
            WriteConversationMessage("Assistant", response.Content);
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

    private async Task<ClientError?> PlaySpokenOutputAsync(
        ConversationResponse response,
        CancellationToken cancellationToken)
    {
        if (!IsSpokenOutputEligible(response))
        {
            return null;
        }

        _spokenOutputCancellationRecorded = false;
        var playbackStarted = false;
        try
        {
            var preparation = await _spokenOutput.PrepareAsync(response.Content!, cancellationToken);
            if (preparation.Kind == SpokenOutputPreparationKind.SynthesisFailed)
            {
                return new ClientError(
                    "speech_synthesis_failed",
                    "The response was shown, but spoken output could not be prepared.");
            }

            if (preparation.Kind == SpokenOutputPreparationKind.Cancelled)
            {
                return new ClientError(
                    "speech_output_cancelled",
                    "The response was shown, but spoken output was cancelled.");
            }

            if (preparation.Kind != SpokenOutputPreparationKind.Prepared ||
                preparation.PreparedOutput is null)
            {
                return null;
            }

            await using var preparedOutput = preparation.PreparedOutput;
            playbackStarted = true;
            BeginActivity(TerminalClientActivity.PlayingVoice);
            var playback = await preparedOutput.PlayAsync(cancellationToken);
            return playback.Kind switch
            {
                SpokenOutputPlaybackKind.Completed => null,
                SpokenOutputPlaybackKind.PlaybackFailed => new ClientError(
                    "speech_playback_failed",
                    "The response was shown, but spoken output could not be played."),
                SpokenOutputPlaybackKind.Cancelled => new ClientError(
                    "speech_output_cancelled",
                    "The response was shown, but spoken output was cancelled."),
                _ => throw new InvalidOperationException("The spoken-output playback result was not recognized."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_stateCoordinator.Current.Error?.IsUncertain != true)
            {
                RecordError(
                    new ClientError("operation_cancelled", "The spoken output was cancelled."),
                    "speech_output",
                    canBeUncertain: false);
            }

            _spokenOutputCancellationRecorded = true;
            throw;
        }
        catch (Exception)
        {
            // The spoken-output boundary owns local provider, player, and artifact-cleanup
            // failures. A response has already been displayed, so none of these failures can
            // invalidate the completed conversational turn or terminate the client.
            return playbackStarted
                ? new ClientError(
                    "speech_playback_failed",
                    "The response was shown, but spoken output could not be played.")
                : new ClientError(
                    "speech_synthesis_failed",
                    "The response was shown, but spoken output could not be prepared.");
        }
    }

    private static bool IsSpokenOutputEligible(ConversationResponse response) =>
        !string.IsNullOrWhiteSpace(response.Content) &&
        response.Confirmation is null &&
        response.Error is null;

    private void WriteError(ClientError error)
    {
        if (_console is IStructuredTerminalConsole structuredConsole)
        {
            structuredConsole.WriteError(error);
            return;
        }

        var suffix = error.IsUncertain ? " The server may have received the operation; it was not retried." : string.Empty;
        _console.WriteLine($"Error ({error.Code}): {error.Message}{suffix}");
    }

    private string? ReadLine(TerminalInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_console is IStructuredTerminalConsole structuredConsole)
        {
            return structuredConsole.ReadLine(request);
        }

        _console.Write(request.Prompt);
        return _console.ReadLine();
    }

    private string ReadSecret(TerminalInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_console is IStructuredTerminalConsole structuredConsole)
        {
            return structuredConsole.ReadSecret(request);
        }

        _console.Write(request.Prompt);
        return _console.ReadSecret();
    }

    private void WriteConversationMessage(string role, string content)
    {
        if (_console is IStructuredTerminalConsole structuredConsole)
        {
            structuredConsole.WriteConversationMessage(role, content);
            return;
        }

        _console.WriteLine($"{role}: {content}");
    }

    private void BeginActivity(TerminalClientActivity activity)
    {
        var current = _stateCoordinator.Current;
        var pendingConfirmation = activity == TerminalClientActivity.AwaitingConfirmation
            ? current.PendingConfirmation
            : null;
        MoveTo(
            TerminalClientLifecycle.Ready,
            activity,
            current.Error,
            current.Provider,
            current.ConversationId,
            pendingConfirmation);
    }

    private void Ready(
        string? provider,
        Guid? conversationId,
        TerminalClientPendingConfirmation? pendingConfirmation,
        bool clearError,
        TerminalClientOperationError? errorOverride = null)
    {
        var current = _stateCoordinator.Current;
        MoveTo(
            TerminalClientLifecycle.Ready,
            TerminalClientActivity.None,
            errorOverride ?? (clearError ? null : current.Error),
            provider,
            conversationId,
            pendingConfirmation,
            replaceConversation: true);
    }

    private void RecordError(
        ClientError error,
        string operation,
        bool canBeUncertain,
        TerminalClientErrorSeverity severity = TerminalClientErrorSeverity.Recoverable)
    {
        var current = _stateCoordinator.Current;
        MoveTo(
            TerminalClientLifecycle.Ready,
            TerminalClientActivity.None,
            new TerminalClientOperationError(
                severity,
                canBeUncertain && error.IsUncertain,
                error.Code,
                error.Message,
                operation),
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
            TerminalClientErrorSeverity.Recoverable,
            false,
            error.Code,
            error.Message,
            operation);

    private static TerminalClientOperationError? ToOperationError(
        ClientError? error,
        string operation) => error is null
        ? null
        : new TerminalClientOperationError(
            TerminalClientErrorSeverity.Recoverable,
            error.IsUncertain,
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
                TerminalClientErrorSeverity.Blocking,
                error.IsUncertain,
                error.Code,
                error.Message,
                operation),
            current.Provider,
            current.ConversationId,
            pendingConfirmation: null);
    }

    private void HandleCancellation()
    {
        if (_spokenOutputCancellationRecorded)
        {
            return;
        }

        var current = _stateCoordinator.Current;
        if (current.Lifecycle == TerminalClientLifecycle.Connecting)
        {
            Block(new ClientError("operation_cancelled", "The operation was cancelled."), "health");
            return;
        }

        if (current.Lifecycle == TerminalClientLifecycle.Authenticating)
        {
            Block(new ClientError("operation_cancelled", "The operation was cancelled."), "authentication");
            return;
        }

        if (current.Lifecycle != TerminalClientLifecycle.Ready)
        {
            return;
        }

        if (current.Error?.IsUncertain == true)
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
            pendingConfirmation,
            _spokenOutput.State);
        if (!_stateCoordinator.TryTransition(next))
        {
            throw new InvalidOperationException("The terminal client requested an invalid state transition.");
        }
    }

    private sealed record CredentialAcquisitionResult(
        PrivateClientCredential? Credential,
        ClientError? Error,
        bool IsCancelled)
    {
        public static CredentialAcquisitionResult Cancelled { get; } = new(null, null, true);

        public static CredentialAcquisitionResult Obtained(PrivateClientCredential credential) =>
            new(credential, null, false);

        public static CredentialAcquisitionResult Failed(ClientError error) => new(null, error, false);
    }

    private sealed record LastConversationUpdateResult(
        PrivateClientCredential Credential,
        ClientError? Error);

    private static string GetOperation(TerminalClientActivity activity) => activity switch
    {
        TerminalClientActivity.SendingTurn => "turn",
        TerminalClientActivity.ResolvingConfirmation => "confirmation",
        TerminalClientActivity.CompletingConversation => "completion",
        TerminalClientActivity.PlayingVoice => "speech_output",
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
