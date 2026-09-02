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
            TerminalClientOptions.Parse(["--provider=fake", "--scenario=direct"]));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[2].Body.RootElement.TryGetProperty("conversationId", out _));
        Assert.Equal(
            conversationId,
            handler.Requests[3].Body.RootElement.GetProperty("conversationId").GetGuid());
        Assert.Contains("Conversation error: provider_timeout", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
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

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage SessionResponse(string accessToken) => JsonResponse(
        HttpStatusCode.OK,
        $$"""{ "accessToken": "{{accessToken}}", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }""");

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://localhost:5100/"),
    };

    private static TerminalClientApplication CreateApplication(
        HttpClient httpClient,
        ITerminalConsole console,
        IPrivateClientCredentialStore? credentialStore = null) => new(
        new PrivateApiClient(httpClient),
        console,
        TerminalClientOptions.Parse(["--provider=fake"]),
        credentialStore);

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
