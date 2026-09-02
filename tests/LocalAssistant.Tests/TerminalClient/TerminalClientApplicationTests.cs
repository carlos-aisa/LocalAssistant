using System.Net;
using System.Text;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientApplicationTests
{
    [Fact]
    public async Task FakeConversationReusesConversationIdAndDoesNotWriteTheCredential()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First message", "Second message", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake", "--scenario=direct"]));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[2].Body.RootElement.TryGetProperty("conversationId", out _));
        Assert.Equal(
            conversationId,
            handler.Requests[3].Body.RootElement.GetProperty("conversationId").GetGuid());
        Assert.Contains("Provider: fake (scenario: direct)", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: First response", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-a", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredConversationErrorReusesConversationIdForTheNextMessage()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(
                HttpStatusCode.GatewayTimeout,
                FailedConversationResponseJson(conversationId, "provider_timeout")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First message", "Second message", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake", "--scenario=direct"]),
            new ManualPrivateClientCredentialStore(),
            sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[2].Body.RootElement.TryGetProperty("conversationId", out _));
        Assert.Equal(
            conversationId,
            handler.Requests[3].Body.RootElement.GetProperty("conversationId").GetGuid());
        Assert.Contains("Conversation error: provider_timeout", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
            snapshot.Error?.Category == TerminalClientErrorCategory.Recoverable &&
            snapshot.Error.Code == "provider_timeout" &&
            snapshot.ConversationId == conversationId);
    }

    [Fact]
    public async Task PendingConfirmationCanBeRejected()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "reject", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake"]));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Confirmation required", console.Output, StringComparison.Ordinal);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ConfirmationRequiresAnExplicitDecision()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "typo", "reject", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[3].Body.RootElement.GetProperty("approved").GetBoolean());
        Assert.Contains("Type approve, reject, or cancel.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredCredentialRejectedByTheServerOffersRecoveryAndPersistsTheReplacement()
    {
        var store = new TestCredentialStore(new PrivateClientCredential("stored-client", "stored-credential"));
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("replacement-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", null], "replacement-credential");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("stored-client", handler.Requests[1].Body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("client-a", handler.Requests[2].Body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal(new PrivateClientCredential("client-a", "replacement-credential"), store.SavedCredential);
        Assert.Contains("Recover with pairing or a manual credential", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingIsPersistedOnlyAfterTheNewCredentialOpensASession()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "paired-client", "displayName": "Desktop", "credential": "paired-credential" }
                """),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([string.Empty, "Desktop", null], "pairing-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new PrivateClientCredential("paired-client", "paired-credential"), store.SavedCredential);
        Assert.Equal("pairing-challenge", handler.Requests[1].Body.RootElement.GetProperty("challenge").GetString());
    }

    [Fact]
    public async Task PairingCredentialIsNotPersistedWhenItCannotOpenASession()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "paired-client", "displayName": "Desktop", "credential": "paired-credential" }
                """),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([string.Empty, "Desktop"], "pairing-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(store.SavedCredential);
        Assert.DoesNotContain("pairing-challenge", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("paired-credential", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionRenewalRetriesAMessageExactlyOnce()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Recovered response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[2].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task SessionRenewalDoesNotLoopWhenTheRetriedMessageIsAlsoRejected()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains("Error (authentication_failed)", console.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/provider ollama")]
    [InlineData("/exit")]
    public async Task CommandsCompleteAnActiveConversationWithNoContentResponse(string command)
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", command, null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal($"/api/conversations/{conversationId}/completion", handler.Requests[3].Path);
    }

    [Fact]
    public async Task CompletionRenewsTheSessionOnceBeforeRetrying()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", "/new", null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
    }

    [Fact]
    public async Task NewCommandClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["Hello", "/new", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(store.Credential);
        Assert.Null(store.Credential.LastConversationId);
    }

    [Fact]
    public async Task ResumeHistoryNotFoundClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "conversationId": "{{conversationId}}",
                  "title": "Previous conversation",
                  "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
                  "indexingRequestedAtUtc": null
                }
                """),
            _ => JsonResponse(HttpStatusCode.NotFound, string.Empty),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["R", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(store.Credential);
        Assert.Null(store.Credential.LastConversationId);
    }

    [Fact]
    public async Task ResumeLoadsAndDisplaysThePublicHistory()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "items": [
                    { "role": "user", "content": "Previous question" },
                    { "role": "assistant", "content": "Previous answer" }
                  ],
                  "nextCursor": null
                }
                """),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["R", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
        Assert.Contains("user: Previous question", console.Output, StringComparison.Ordinal);
        Assert.Contains("assistant: Previous answer", console.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ResumeValidationFailuresOtherThanNotFoundPreserveTheLastConversationId(
        HttpStatusCode statusCode)
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(statusCode, string.Empty),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task ProviderCommandClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["Hello", "/provider ollama", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task NewChoiceAtStartupClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["N", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task SelectingAnotherConversationCompletesTheCurrentConversationBeforeLoadingHistory()
    {
        var currentConversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var selectedConversationId = Guid.Parse("4b384d77-2681-4d55-8d18-cb27f74cdd1b");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(currentConversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "items": [
                    {
                      "conversationId": "{{selectedConversationId}}",
                      "title": "Selected conversation",
                      "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
                      "indexingRequestedAtUtc": null
                    }
                  ],
                  "nextCursor": null
                }
                """),
            _ => ConversationDetailsResponse(selectedConversationId, "Selected conversation"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "items": [ { "role": "assistant", "content": "Selected history" } ],
                  "nextCursor": null
                }
                """),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["Hello", "/conversations", "1", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/conversations/{currentConversationId}/completion", handler.Requests[5].Path);
        Assert.Equal($"/api/conversations/{selectedConversationId}/history", handler.Requests[6].Path);
        Assert.Equal(selectedConversationId, store.Credential!.LastConversationId);
        Assert.Contains("assistant: Selected history", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeRenewsTheBearerWhenLoadingConversationDetails()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["N", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Bearer expired-token", handler.Requests[2].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task ResumeRenewsTheBearerWhenLoadingConversationHistory()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "items": [ { "role": "assistant", "content": "History" } ], "nextCursor": null }
                """),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["R", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
    }

    [Fact]
    public async Task ConversationSelectorRenewsTheBearerWhenListing()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "items": [], "nextCursor": null }"""),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["/conversations", "C", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Bearer expired-token", handler.Requests[2].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task ConversationSelectorRequestsTheNextPageBeforeSelection()
    {
        var selectedConversationId = Guid.Parse("4b384d77-2681-4d55-8d18-cb27f74cdd1b");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "items": [],
                  "nextCursor": "opaque-next-page"
                }
                """),
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "items": [
                    {
                      "conversationId": "{{selectedConversationId}}",
                      "title": "Second page",
                      "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
                      "indexingRequestedAtUtc": null
                    }
                  ],
                  "nextCursor": null
                }
                """),
            _ => ConversationDetailsResponse(selectedConversationId, "Second page"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "items": [ { "role": "assistant", "content": "History" } ], "nextCursor": null }
                """),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["/conversations", "N", "1", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(selectedConversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task InvalidResumeDetailsResponsePreservesTheLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, "{}"),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task ResumeTimeoutPreservesTheLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => throw new TaskCanceledException(),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task FailedCompletionRetainsTheRenewedSessionForTheNextMessage()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", "/new", "Again", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(7, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[6].Authorization);
    }

    [Fact]
    public async Task ConfirmationRenewsTheSessionOnceBeforeRetrying()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder created")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "approve", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
    }

    [Fact]
    public async Task RotationValidatesAndPersistsTheReplacementCredential()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "client-a", "displayName": "Desktop", "credential": "replacement-credential" }
                """),
            _ => SessionResponse("replacement-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate", null],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("client-a", handler.Requests[2].Body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal(new PrivateClientCredential("client-a", "replacement-credential"), store.SavedCredential);
        Assert.DoesNotContain("credential-a", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("rotation-challenge", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-credential", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("old-token", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-token", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationForAnotherClientDoesNotReplaceTheLocalCredential()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "other-client", "displayName": "Desktop", "credential": "other-credential" }
                """),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate", null],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new PrivateClientCredential("client-a", "credential-a"), store.SavedCredential);
        Assert.Contains("Credential rotation was rejected", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationWarnsWhenTheReplacementCredentialCannotBePersisted()
    {
        var store = new TestCredentialStore
        {
            FailAfterFirstSave = true,
        };
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "client-a", "displayName": "Desktop", "credential": "replacement-credential" }
                """),
            _ => SessionResponse("replacement-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate", null],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("The credential was rotated but could not be stored", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevocationForTheCurrentClientDeletesOnlyItsLocalCredential()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "clientId": "client-a" }"""),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin revoke", "REVOKE"],
            "credential-a",
            "revocation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("client-a", handler.Requests[2].Body.RootElement.GetProperty("clientId").GetString());
        Assert.True(store.Deleted);
    }

    [Fact]
    public async Task RevocationForAnotherClientDoesNotDeleteTheLocalCredential()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "clientId": "other-client" }"""),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin revoke", "REVOKE", null],
            "credential-a",
            "revocation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(store.Deleted);
        Assert.Contains("Client revocation was rejected", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPublishesTheCompleteStartupAndClosingSequence()
    {
        var sink = new RecordingTerminalClientStateSink();
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
        [
            (TerminalClientLifecycle.Disconnected, TerminalClientActivity.None),
            (TerminalClientLifecycle.Connecting, TerminalClientActivity.None),
            (TerminalClientLifecycle.Authenticating, TerminalClientActivity.None),
            (TerminalClientLifecycle.Ready, TerminalClientActivity.None),
            (TerminalClientLifecycle.Ready, TerminalClientActivity.ResumingConversation),
            (TerminalClientLifecycle.Ready, TerminalClientActivity.None),
            (TerminalClientLifecycle.Closing, TerminalClientActivity.None),
            (TerminalClientLifecycle.Closed, TerminalClientActivity.None),
        ],
        sink.Snapshots.Select(snapshot => (snapshot.Lifecycle, snapshot.Activity)));
    }

    [Fact]
    public async Task ExitCompletesBeforePublishingTheClosingSequence()
    {
        var conversationId = Guid.Parse("bc6b7aaf-3020-44c5-a3b9-47e9db32f24b");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Message", "/exit"], "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var completionIndex = sink.Snapshots.FindIndex(snapshot =>
            snapshot.Activity == TerminalClientActivity.CompletingConversation);
        Assert.True(completionIndex >= 0);
        Assert.Equal(TerminalClientLifecycle.Ready, sink.Snapshots[completionIndex + 1].Lifecycle);
        Assert.Equal(TerminalClientActivity.None, sink.Snapshots[completionIndex + 1].Activity);
        Assert.Equal(TerminalClientLifecycle.Closing, sink.Snapshots[^2].Lifecycle);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task UncertainTurnFailureIsPublishedWithoutBlockingTheClient()
    {
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Message", null], "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var uncertain = Assert.Single(
            sink.Snapshots,
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Error?.Category == TerminalClientErrorCategory.Uncertain);
        Assert.Equal("turn", uncertain.Error!.Operation);
        Assert.Equal(TerminalClientLifecycle.Ready, uncertain.Lifecycle);
        Assert.Equal(TerminalClientActivity.None, uncertain.Activity);
    }

    [Fact]
    public async Task SuccessfulTurnClearsThePreviousRecoverableError()
    {
        var conversationId = Guid.Parse("ee13aa74-1971-4fcb-817e-96621190408e");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Forbidden, string.Empty),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Recovered")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First message", "Second message", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var errorIndex = sink.Snapshots.FindIndex(snapshot =>
            snapshot.Error?.Category == TerminalClientErrorCategory.Recoverable &&
            snapshot.Error.Operation == "turn");
        Assert.True(errorIndex >= 0);
        Assert.Contains(
            sink.Snapshots.Skip(errorIndex + 1),
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Activity == TerminalClientActivity.None &&
                snapshot.Error is null &&
                snapshot.ConversationId == conversationId);
    }

    [Fact]
    public async Task ReadFailureIsRecoverableRatherThanUncertain()
    {
        var conversationId = Guid.Parse("24cc7ee6-91cf-4464-9909-e7f7b1159191");
        var sink = new RecordingTerminalClientStateSink();
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var error = Assert.Single(
            sink.Snapshots,
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Error?.Operation == "resume");
        Assert.Equal(TerminalClientErrorCategory.Recoverable, error.Error!.Category);
    }

    [Fact]
    public async Task BlockingHealthFailureStillClosesTheClient()
    {
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.ServiceUnavailable, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([]);
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        var blocked = Assert.Single(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked);
        Assert.Equal(TerminalClientErrorCategory.Blocking, blocked.Error!.Category);
        Assert.Equal(TerminalClientLifecycle.Closing, sink.Snapshots[^2].Lifecycle);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task CompletionFailureAfterDispatchIsPublishedAsUncertain()
    {
        var conversationId = Guid.Parse("989452a2-5cf8-4fdf-a83c-59d43bdad08f");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Message", "/exit", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var error = Assert.Single(
            sink.Snapshots,
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Error?.Operation == "completion");
        Assert.Equal(TerminalClientErrorCategory.Uncertain, error.Error!.Category);
        Assert.Equal(TerminalClientActivity.None, error.Activity);
    }

    [Fact]
    public async Task CapturedCancellationStillPublishesClosingAndClosed()
    {
        using var cancellationSource = new CancellationTokenSource();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ =>
            {
                cancellationSource.Cancel();
                return JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }""");
            },
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([]);
        var application = CreateApplication(
            httpClient,
            console,
            new CancellationAwareCredentialStore(),
            sink);

        var exitCode = await application.RunAsync(cancellationSource.Token);

        Assert.Equal(2, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Code == "operation_cancelled");
        Assert.Equal(TerminalClientLifecycle.Closing, sink.Snapshots[^2].Lifecycle);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task ThrowingStateSinkDoesNotInterruptTheClient()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", null], "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: new ThrowingTerminalClientStateSink());

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage SessionResponse(string accessToken) => JsonResponse(
        HttpStatusCode.OK,
        $$"""{ "accessToken": "{{accessToken}}", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }""");

    private static HttpResponseMessage ConversationDetailsResponse(Guid conversationId, string title) => JsonResponse(
        HttpStatusCode.OK,
        $$"""
        {
          "conversationId": "{{conversationId}}",
          "title": "{{title}}",
          "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
          "indexingRequestedAtUtc": null
        }
        """);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://localhost:5100/"),
    };

    private static TerminalClientApplication CreateApplication(
        HttpClient httpClient,
        ITerminalConsole console,
        IPrivateClientCredentialStore? credentialStore = null,
        ITerminalClientStateSink? stateSink = null)
    {
        var options = TerminalClientOptions.Parse(["--provider=fake"]);
        return stateSink is null
            ? new TerminalClientApplication(new PrivateApiClient(httpClient), console, options, credentialStore)
            : new TerminalClientApplication(
                new PrivateApiClient(httpClient),
                console,
                options,
                credentialStore ?? new ManualPrivateClientCredentialStore(),
                stateSink);
    }

    private static string ConversationResponseJson(Guid conversationId, string content) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": "{{content}}",
          "tools": [
            {
              "toolCallId": "tool-1",
              "toolName": "current_time",
              "succeeded": true,
              "durationMilliseconds": 2,
              "errorCode": null
            }
          ],
          "iterations": 1,
          "timings": {},
          "error": null,
          "confirmation": null
        }
        """;

    private static string ConfirmationResponseJson(Guid conversationId) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": null,
          "tools": [],
          "iterations": 1,
          "timings": {},
          "error": null,
          "confirmation": {
            "confirmationId": "7a1a5909-0a04-4a30-b4d2-82b28c40f146",
            "toolCallId": "tool-1",
            "toolName": "create_reminder",
            "arguments": {},
            "expiresAtUtc": "2026-09-01T12:00:00+00:00"
          }
        }
        """;

    private static string FailedConversationResponseJson(Guid conversationId, string errorCode) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": null,
          "tools": [],
          "iterations": 1,
          "timings": {},
          "error": {
            "code": "{{errorCode}}",
            "message": "The conversation could not be completed.",
            "toolName": null
          },
          "confirmation": null
        }
        """;
}

internal sealed class ScriptedTerminalConsole : ITerminalConsole, IDisposable
{
    private readonly Queue<string?> _lines;
    private readonly Queue<string> _secrets;
    private readonly StringWriter _writer = new();

    public ScriptedTerminalConsole(IEnumerable<string?> lines, params string[] secrets)
    {
        _lines = new Queue<string?>(lines);
        _secrets = new Queue<string>(secrets);
    }

    public string Output => _writer.ToString();

    public string? ReadLine() => _lines.Dequeue();

    public string ReadSecret() => _secrets.Dequeue();

    public void Write(string value) => _writer.Write(value);

    public void WriteLine(string value) => _writer.WriteLine(value);

    public void Dispose() => _writer.Dispose();
}

internal sealed class TestCredentialStore : IPrivateClientCredentialStore
{
    private int _saveCount;

    public TestCredentialStore(PrivateClientCredential? credential = null)
    {
        Credential = credential;
    }

    public PrivateClientCredential? Credential { get; private set; }

    public PrivateClientCredential? SavedCredential { get; private set; }

    public bool SaveResult { get; set; } = true;

    public bool FailAfterFirstSave { get; set; }

    public bool Deleted { get; private set; }

    public Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Credential);

    public Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken)
    {
        SavedCredential = credential;
        _saveCount++;
        var saved = SaveResult && (!FailAfterFirstSave || _saveCount == 1);
        if (saved)
        {
            Credential = credential;
        }

        return Task.FromResult(saved);
    }

    public Task<bool> DeleteAsync(CancellationToken cancellationToken)
    {
        Deleted = true;
        Credential = null;
        return Task.FromResult(true);
    }
}

internal sealed class RecordingTerminalClientStateSink : ITerminalClientStateSink
{
    public List<TerminalClientStateSnapshot> Snapshots { get; } = [];

    public void OnStateChanged(TerminalClientStateSnapshot snapshot) => Snapshots.Add(snapshot);
}

internal sealed class ThrowingTerminalClientStateSink : ITerminalClientStateSink
{
    public void OnStateChanged(TerminalClientStateSnapshot snapshot) =>
        throw new InvalidOperationException("Observer failure.");
}

internal sealed class CancellationAwareCredentialStore : IPrivateClientCredentialStore
{
    public Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PrivateClientCredential?>(null);
    }

    public Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> DeleteAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
