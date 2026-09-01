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
    public async Task PendingConfirmationStopsTheTextOnlyIncrement()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake"]));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot resolve it yet", console.Output, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

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
    private readonly string _credential;
    private readonly StringWriter _writer = new();

    public ScriptedTerminalConsole(IEnumerable<string?> lines, string credential)
    {
        _lines = new Queue<string?>(lines);
        _credential = credential;
    }

    public string Output => _writer.ToString();

    public string? ReadLine() => _lines.Dequeue();

    public string ReadSecret() => _credential;

    public void Write(string value) => _writer.Write(value);

    public void WriteLine(string value) => _writer.WriteLine(value);

    public void Dispose() => _writer.Dispose();
}
