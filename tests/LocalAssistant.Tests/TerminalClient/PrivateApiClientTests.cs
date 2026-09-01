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
    public async Task GatewayTimeoutForATurnIsReportedAsUncertainWithoutRetry()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.GatewayTimeout, "{}"),
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
        Assert.Equal("provider_timeout", result.Error!.Code);
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
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
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
