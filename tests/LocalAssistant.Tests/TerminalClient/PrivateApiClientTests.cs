using System.Net;
using System.Text;
using System.Text.Json;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Security.PrivateClients;
using LocalAssistant.TerminalClient;
using LocalAssistant.Tests.Api;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class PrivateApiClientTests
{
    [Fact]
    public async Task SendsBearerOnlyForConversationMessages()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656"))),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var health = await client.CheckHealthAsync(CancellationToken.None);
        var session = await client.CreateSessionAsync("client-a", "credential-a", CancellationToken.None);
        var message = await client.SendMessageAsync(
            session.Value!.AccessToken,
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.True(health.IsSuccess);
        Assert.True(session.IsSuccess);
        Assert.True(message.IsSuccess);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/health", request.Path);
                Assert.Null(request.Authorization);
            },
            request =>
            {
                Assert.Equal("/api/private/sessions", request.Path);
                Assert.Null(request.Authorization);
                Assert.Equal("client-a", request.Body.RootElement.GetProperty("clientId").GetString());
                Assert.Equal("credential-a", request.Body.RootElement.GetProperty("credential").GetString());
            },
            request =>
            {
                Assert.Equal("/api/conversations/messages", request.Path);
                Assert.Equal("Bearer session-token", request.Authorization);
                Assert.Equal("Hello", request.Body.RootElement.GetProperty("message").GetString());
                Assert.False(request.Body.RootElement.TryGetProperty("conversationId", out _));
            });
    }

    [Fact]
    public async Task StructuredGatewayTimeoutPreservesTheConversationResponseWithoutRetry()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(
                HttpStatusCode.GatewayTimeout,
                FailedConversationResponseJson(conversationId, "provider_timeout")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(conversationId, result.Value!.ConversationId);
        Assert.Equal("provider_timeout", result.Value.Error!.Code);
        Assert.Single(result.Value.Tools);
        Assert.Equal(2, result.Value.Iterations);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StructuredForbiddenPreservesTheConversationResponseWithoutRetry()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(
                HttpStatusCode.Forbidden,
                FailedConversationResponseJson(conversationId, "scope_not_granted")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(conversationId, result.Value!.ConversationId);
        Assert.Equal("scope_not_granted", result.Value.Error!.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransportTimeoutForATurnIsReportedAsUncertainWithoutRetry()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => throw new TaskCanceledException(),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("request_timeout", result.Error!.Code);
        Assert.True(result.Error.IsUncertain);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EmptyUnauthorizedResponseIsNotUncertain()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("authentication_failed", result.Error!.Code);
        Assert.False(result.Error.IsUncertain);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "validation_error")]
    [InlineData(HttpStatusCode.Forbidden, "authorization_failed")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.Conflict, "conflict")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "validation_error")]
    public async Task RejectedResponseWithoutConversationContractIsNotUncertain(
        HttpStatusCode statusCode,
        string expectedErrorCode)
    {
        var content = statusCode == HttpStatusCode.BadRequest
            ? """{ "title": "One or more validation errors occurred.", "status": 400 }"""
            : """{ "title": "The request was rejected." }""";
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(statusCode, content),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedErrorCode, result.Error!.Code);
        Assert.False(result.Error.IsUncertain);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidServerErrorForATurnIsReportedAsUncertainWithoutRetry()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.InternalServerError, "{}"),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("http_error", result.Error!.Code);
        Assert.True(result.Error.IsUncertain);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidSuccessfulResponseForATurnIsReportedAsUncertainWithoutRetry()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, "{}"),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        var client = new PrivateApiClient(httpClient);

        var result = await client.SendMessageAsync(
            "session-token",
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_response", result.Error!.Code);
        Assert.True(result.Error.IsUncertain);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://192.168.1.10:5100")]
    [InlineData("https://example.com")]
    [InlineData("ftp://localhost")]
    public void OptionsRejectNonLoopbackOrUnsupportedBaseUrls(string baseUrl)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TerminalClientOptions.Parse(["--base-url=" + baseUrl]));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedArgumentDoesNotEchoItsValue()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TerminalClientOptions.Parse(["--credential=must-not-appear"]));

        Assert.DoesNotContain("must-not-appear", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesThePublishedPrivateSessionAndMessageContractsWithTheInProcessApi()
    {
        using var factory = new LocalAssistantApiFactory();
        var installation = factory.Services.GetRequiredService<IInstallationIdentityStore>();
        var owner = await installation.BootstrapAsync(CancellationToken.None);
        var authentication = factory.Services.GetRequiredService<PrivateClientAuthenticationService>();
        var challenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var credential = await authentication.CompleteClientPairingAsync(
            challenge.Secret,
            owner.OwnerPrincipalId!,
            "Terminal client contract test",
            CancellationToken.None);
        using var httpClient = new HttpClient(factory.Server.CreateHandler())
        {
            BaseAddress = factory.Server.BaseAddress,
        };
        var client = new PrivateApiClient(httpClient);

        var health = await client.CheckHealthAsync(CancellationToken.None);
        var session = await client.CreateSessionAsync(
            credential!.Client.ClientId,
            credential.Secret,
            CancellationToken.None);
        var message = await client.SendMessageAsync(
            session.Value!.AccessToken,
            new SendMessageRequest("Hello", null, "fake", "direct"),
            CancellationToken.None);

        Assert.True(health.IsSuccess);
        Assert.True(session.IsSuccess);
        Assert.True(message.IsSuccess);
        Assert.Equal("Fake response: Hello", message.Value!.Content);
    }

    [Fact]
    public void TerminalClientProjectDoesNotReferenceServerProjects()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "projects",
            "LocalAssistant.TerminalClient.csproj");
        var project = File.ReadAllText(path);

        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
        Assert.Contains("System.Security.Cryptography.ProtectedData", project, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static string ConversationResponseJson(Guid conversationId) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": "Fake response: Hello",
          "tools": [],
          "iterations": 1,
          "timings": {},
          "error": null,
          "confirmation": null
        }
        """;

    private static string FailedConversationResponseJson(Guid conversationId, string errorCode) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": null,
          "tools": [
            {
              "toolCallId": "tool-1",
              "toolName": "search_documents",
              "succeeded": false,
              "durationMilliseconds": 2,
              "errorCode": "provider_error"
            }
          ],
          "iterations": 2,
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

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<CapturedRequest, HttpResponseMessage>> _responses;

    public RecordingHttpMessageHandler(IEnumerable<Func<CapturedRequest, HttpResponseMessage>> responses)
    {
        _responses = new Queue<Func<CapturedRequest, HttpResponseMessage>>(responses);
    }

    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? JsonDocument.Parse("null")
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        var captured = new CapturedRequest(
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            body);
        Requests.Add(captured);

        return _responses.Dequeue()(captured);
    }
}

internal sealed record CapturedRequest(string Path, string? Authorization, JsonDocument Body);
