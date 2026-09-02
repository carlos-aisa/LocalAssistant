using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Security.PrivateClients;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Api;

public sealed class ConversationEndpointTests : IClassFixture<LocalAssistantApiFactory>
{
    private const string LocalApiKey = "local-assistant-test-key";
    private static readonly string LocalApiKeyHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(LocalApiKey)));
    private readonly HttpClient _client;
    private readonly LocalAssistantApiFactory _factory;

    public ConversationEndpointTests(LocalAssistantApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DirectScenarioReturnsFinalResponseWithoutTools()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello LocalAssistant", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var root = body.RootElement;
        Assert.NotEqual(Guid.Empty, root.GetProperty("conversationId").GetGuid());
        Assert.Equal("Fake response: Hello LocalAssistant", root.GetProperty("content").GetString());
        Assert.Equal(1, root.GetProperty("iterations").GetInt32());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task PrivateBearerSessionAuthenticatesAnApiRequest()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
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
            "Test client",
            CancellationToken.None);
        var session = await authentication.CreateSessionAsync(
            credential!.Client.ClientId,
            credential.Secret,
            TimeSpan.FromHours(1),
            CancellationToken.None);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/messages")
        {
            Content = JsonContent.Create(new { message = "Hello", scenario = "direct" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session!.Token);
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PrivateBearerPreservesOwnerClientSessionAndServerGrantedScopes()
    {
        using var factory = new LocalAssistantApiFactory();
        var session = await CreatePrivateBearerAsync(factory);
        var context = new DefaultHttpContext
        {
            RequestServices = factory.Services,
        };
        context.Request.Headers.Authorization = $"Bearer {session.Token}";

        var authentication = await context.AuthenticateAsync(
            PrivateBearerAuthenticationDefaults.SchemeName);

        Assert.True(authentication.Succeeded);
        Assert.Equal(session.OwnerPrincipalId, authentication.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(session.ClientId, authentication.Principal.FindFirstValue(
            PrivateBearerAuthenticationDefaults.ClientIdClaimType));
        Assert.Equal(session.SessionId, authentication.Principal.FindFirstValue(
            PrivateBearerAuthenticationDefaults.SessionIdClaimType));
        Assert.True(authentication.Principal.HasClaim(
            LocalApiKeyAuthenticationDefaults.ScopeClaimType,
            "memory.personal.write"));
    }

    [Fact]
    public async Task PrivateBearerPersistsAndCompletesOnlyItsOwnersConversation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:ConversationPersistence:Enabled", "true");
            builder.UseSetting(
                "LocalAssistant:ConversationPersistence:DatabasePath",
                Path.Combine(directory.Path, "conversations.db"));
        });
        using var authenticatedClient = factory.CreateClient();
        var session = await CreatePrivateBearerAsync(factory);
        authenticatedClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.Token);

        using var messageResponse = await authenticatedClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Private message", scenario = "direct" },
            CancellationToken.None);
        using var messageBody = await JsonDocument.ParseAsync(
            await messageResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = messageBody.RootElement.GetProperty("conversationId").GetGuid();
        using var completionResponse = await authenticatedClient.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);

        using var anonymousClient = factory.CreateClient();
        var unknownConversationId = Guid.NewGuid();
        using var unknownContinuation = await anonymousClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Unknown continuation", conversationId = unknownConversationId, scenario = "direct" },
            CancellationToken.None);
        using var anonymousContinuation = await anonymousClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Unauthorized continuation", conversationId, scenario = "direct" },
            CancellationToken.None);
        using var anonymousCompletion = await anonymousClient.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, messageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, completionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownContinuation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, anonymousContinuation.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCompletion.StatusCode);
        using var unknownBody = JsonDocument.Parse(
            await unknownContinuation.Content.ReadAsStringAsync(CancellationToken.None));
        using var inaccessibleBody = JsonDocument.Parse(
            await anonymousContinuation.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(
            unknownBody.RootElement.GetProperty("error").GetProperty("code").GetString(),
            inaccessibleBody.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExpiredRevokedAndRotatedBearerTokensAreRejectedOverHttp()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
        var authentication = factory.Services.GetRequiredService<PrivateClientAuthenticationService>();
        var expired = await CreatePrivateBearerAsync(factory, TimeSpan.FromMinutes(1));
        var clock = Assert.IsType<ManualTimeProvider>(factory.Services.GetRequiredService<TimeProvider>());
        clock.Advance(TimeSpan.FromMinutes(1));

        using var expiredResponse = await SendBearerConversationAsync(client, expired.Token);

        var active = await CreatePrivateBearerAsync(factory, TimeSpan.FromHours(1));
        var revocationChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RevokeClient,
            active.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.NotNull(await authentication.RevokeClientAsync(
            revocationChallenge.Secret,
            active.ClientId,
            CancellationToken.None));
        using var revokedResponse = await SendBearerConversationAsync(client, active.Token);

        var rotating = await CreatePrivateBearerAsync(factory, TimeSpan.FromHours(1));
        var rotationChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RotateCredential,
            rotating.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        Assert.NotNull(await authentication.RotateCredentialAsync(
            rotationChallenge.Secret,
            rotating.ClientId,
            CancellationToken.None));
        using var rotatedResponse = await SendBearerConversationAsync(client, rotating.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rotatedResponse.StatusCode);
    }

    [Fact]
    public async Task PrivateEndpointsRejectRequestsThatAreNotLoopback()
    {
        using var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILoopbackRequestPolicy>();
                services.AddSingleton<ILoopbackRequestPolicy, RejectingLoopbackRequestPolicy>();
            });
        });
        using var client = factory.CreateClient();

        using var sessionResponse = await client.PostAsJsonAsync(
            "/api/private/sessions",
            new { clientId = "client", credential = "credential" },
            CancellationToken.None);
        using var bearerResponse = await SendBearerConversationAsync(client, "invalid");

        Assert.Equal(HttpStatusCode.NotFound, sessionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerResponse.StatusCode);
    }

    [Fact]
    public async Task InvalidBearerAndMixedCredentialsAreRejectedBeforePublicConversationHandling()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/messages")
        {
            Content = JsonContent.Create(new { message = "Hello", scenario = "direct" }),
        };
        invalidRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid");
        using var invalidResponse = await client.SendAsync(invalidRequest, CancellationToken.None);

        using var mixedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/messages")
        {
            Content = JsonContent.Create(new { message = "Hello", scenario = "direct" }),
        };
        mixedRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid");
        mixedRequest.Headers.Add("X-LocalAssistant-Api-Key", LocalApiKey);
        using var mixedResponse = await client.SendAsync(mixedRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mixedResponse.StatusCode);
    }

    [Fact]
    public async Task ApiKeyDoesNotAuthorizeGeneralEndpointsOutsideTheTestHarness()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("LocalAssistant:Identity:Enabled", "true");
            builder.UseSetting("LocalAssistant:Identity:PrincipalId", "test-owner");
            builder.UseSetting("LocalAssistant:Identity:ApiKeySha256", LocalApiKeyHash);
        }).CreateClient();
        client.DefaultRequestHeaders.Add("X-LocalAssistant-Api-Key", LocalApiKey);

        using var conversationResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", scenario = "direct" },
            CancellationToken.None);
        using var memoryResponse = await client.GetAsync(
            "/api/memories/personal",
            CancellationToken.None);
        using var documentResponse = await client.GetAsync(
            "/api/documents",
            CancellationToken.None);
        using var toolResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, conversationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, memoryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, documentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, toolResponse.StatusCode);
    }

    [Fact]
    public async Task PrivateClientEndpointsPairAndOpenALoopbackSession()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
        var installation = factory.Services.GetRequiredService<IInstallationIdentityStore>();
        var owner = await installation.BootstrapAsync(CancellationToken.None);
        var authentication = factory.Services.GetRequiredService<PrivateClientAuthenticationService>();
        var challenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        using var pairingResponse = await client.PostAsJsonAsync(
            "/api/private/admin/pairings",
            new { challenge = challenge.Secret, displayName = "Test client" },
            CancellationToken.None);
        var pairing = await pairingResponse.Content.ReadFromJsonAsync<PrivateClientCredentialResponse>(
            cancellationToken: CancellationToken.None);
        using var sessionResponse = await client.PostAsJsonAsync(
            "/api/private/sessions",
            new { clientId = pairing!.ClientId, credential = pairing.Credential },
            CancellationToken.None);

        Assert.Equal(InstallationBootstrapStatus.Created, owner.Status);
        Assert.Equal(HttpStatusCode.OK, pairingResponse.StatusCode);
        Assert.NotNull(pairing);
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
    }

    [Fact]
    public async Task PrivateClientAdministrativeEndpointsRotateAndRevokeThePairedClient()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
        var installation = factory.Services.GetRequiredService<IInstallationIdentityStore>();
        var owner = await installation.BootstrapAsync(CancellationToken.None);
        var authentication = factory.Services.GetRequiredService<PrivateClientAuthenticationService>();
        var pairingChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var pairingResponse = await client.PostAsJsonAsync(
            "/api/private/admin/pairings",
            new { challenge = pairingChallenge.Secret, displayName = "Test client" },
            CancellationToken.None);
        var paired = await pairingResponse.Content.ReadFromJsonAsync<PrivateClientCredentialResponse>(
            cancellationToken: CancellationToken.None);
        var rotationChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RotateCredential,
            paired!.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var rotationResponse = await client.PostAsJsonAsync(
            "/api/private/admin/credential-rotations",
            new { challenge = rotationChallenge.Secret, clientId = paired.ClientId },
            CancellationToken.None);
        var rotated = await rotationResponse.Content.ReadFromJsonAsync<PrivateClientCredentialResponse>(
            cancellationToken: CancellationToken.None);
        var revocationChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RevokeClient,
            paired.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var revocationResponse = await client.PostAsJsonAsync(
            "/api/private/admin/client-revocations",
            new { challenge = revocationChallenge.Secret, clientId = paired.ClientId },
            CancellationToken.None);
        var revoked = await revocationResponse.Content.ReadFromJsonAsync<PrivateClientRevocationResponse>(
            cancellationToken: CancellationToken.None);

        Assert.Equal(InstallationBootstrapStatus.Created, owner.Status);
        Assert.Equal(HttpStatusCode.OK, rotationResponse.StatusCode);
        Assert.NotNull(rotated);
        Assert.NotEqual(paired.Credential, rotated.Credential);
        Assert.Equal(HttpStatusCode.OK, revocationResponse.StatusCode);
        Assert.Equal(paired.ClientId, revoked!.ClientId);
        Assert.Null(await authentication.CreateSessionAsync(
            paired.ClientId,
            rotated.Credential,
            TimeSpan.FromHours(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task PrivateClientAdministrativeEndpointsDoNotConsumeChallengesForAnotherClient()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
        var installation = factory.Services.GetRequiredService<IInstallationIdentityStore>();
        await installation.BootstrapAsync(CancellationToken.None);
        var authentication = factory.Services.GetRequiredService<PrivateClientAuthenticationService>();
        var pairingChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        using var pairingResponse = await client.PostAsJsonAsync(
            "/api/private/admin/pairings",
            new { challenge = pairingChallenge.Secret, displayName = "Test client" },
            CancellationToken.None);
        var paired = await pairingResponse.Content.ReadFromJsonAsync<PrivateClientCredentialResponse>(
            cancellationToken: CancellationToken.None);
        var rotationChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RotateCredential,
            paired!.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        using var rotationMismatch = await client.PostAsJsonAsync(
            "/api/private/admin/credential-rotations",
            new { challenge = rotationChallenge.Secret, clientId = "other-client" },
            CancellationToken.None);
        using var rotationSuccess = await client.PostAsJsonAsync(
            "/api/private/admin/credential-rotations",
            new { challenge = rotationChallenge.Secret, clientId = paired.ClientId },
            CancellationToken.None);
        var revocationChallenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.RevokeClient,
            paired.ClientId,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        using var revocationMismatch = await client.PostAsJsonAsync(
            "/api/private/admin/client-revocations",
            new { challenge = revocationChallenge.Secret, clientId = "other-client" },
            CancellationToken.None);
        using var revocationSuccess = await client.PostAsJsonAsync(
            "/api/private/admin/client-revocations",
            new { challenge = revocationChallenge.Secret, clientId = paired.ClientId },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, rotationMismatch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rotationSuccess.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revocationMismatch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revocationSuccess.StatusCode);
    }

    [Fact]
    public async Task TimeScenarioIncludesAuthoritativeTimeBeforeTheProviderToolCall()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var root = body.RootElement;
        Assert.Equal(
            "Current UTC time is 2026-08-17T14:30:00.0000000+00:00.",
            root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("iterations").GetInt32());
        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(
            tools,
            tool => tool.GetProperty("toolCallId").GetString() == "authoritative-current-time" &&
                    tool.GetProperty("toolName").GetString() == "get_current_time" &&
                    tool.GetProperty("succeeded").GetBoolean());
        Assert.Contains(
            tools,
            tool => tool.GetProperty("toolCallId").GetString() == "fake-time-call-1" &&
                    tool.GetProperty("toolName").GetString() == "get_current_time" &&
                    tool.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task TemperatureScenarioExecutesToolAndReturnsDeterministicConversion()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Convert 100 Celsius to Fahrenheit", scenario = "temperature" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var root = body.RootElement;
        Assert.Equal("100 Celsius is 212 fahrenheit.", root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("iterations").GetInt32());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("convert_temperature", tool.GetProperty("toolName").GetString());
        Assert.True(tool.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task UnknownScenarioReturnsValidationProblem()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", scenario = "unknown" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void RelativeDocumentsRootPreventsTheApplicationFromStarting()
    {
        using var factory = new LocalAssistantApiFactory()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("LocalAssistant:DocumentSources:DocumentsRoot", "documents"));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Configured documents root must be an existing absolute directory.", exception.Message);
    }

    [Fact]
    public async Task UnknownProviderReturnsValidationProblem()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", provider = "unknown" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownToolConfirmationReturnsNotFound()
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/tool-confirmations/{Guid.NewGuid()}/decisions",
            new { approved = true },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationDeletesTheAuthenticatedOwnersPersistedConversation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var client = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a");
        var conversationId = await CreateConversationAsync(client);

        using var deleteRequest = CreateDeleteRequest(conversationId, "true");
        using var deleteResponse = await client.SendAsync(deleteRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task CompletionMarksOnlyTheAuthenticatedOwnersConversationAndIsIdempotent()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var ownerClient = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a");
        var conversationId = await CreateConversationAsync(ownerClient);

        using var firstResponse = await ownerClient.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);
        using var secondResponse = await ownerClient.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);
    }

    [Fact]
    public async Task CompletionDoesNotRevealAnotherPrincipalsConversation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var ownerClient = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a");
        var conversationId = await CreateConversationAsync(ownerClient);
        using var otherClient = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-b");

        using var response = await otherClient.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompletionRequiresAuthenticationAndPersistence()
    {
        var conversationId = Guid.NewGuid();
        using var anonymousResponse = await _client.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, anonymousResponse.StatusCode);

        using var directory = new TemporaryInstallationStateDirectory();
        using var authenticatedFactory = _factory.WithWebHostBuilder(builder =>
        {
            ConfigurePersistentIdentity(
                builder,
                Path.Combine(directory.Path, "conversations.db"),
                "owner-a");
        });
        using var anonymousPersistentClient = authenticatedFactory.CreateClient();
        using var anonymousPersistentResponse = await anonymousPersistentClient.PostAsync(
            $"/api/conversations/{conversationId}/completion",
            content: null,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPersistentResponse.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DeleteConversationRequiresTheExactConfirmationHeader(string? confirmationValue)
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var client = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a");
        var conversationId = await CreateConversationAsync(client);

        using var request = CreateDeleteRequest(conversationId, confirmationValue);
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationRejectsRepeatedConfirmationHeaders()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var client = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a");
        var conversationId = await CreateConversationAsync(client);
        using var request = CreateDeleteRequest(conversationId, "true", "true");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationRequiresAnAuthenticatedPrincipal()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        var databasePath = Path.Combine(directory.Path, "conversations.db");
        using var configuredClient = CreatePersistentIdentityClient(databasePath, "owner-a");
        var conversationId = await CreateConversationAsync(configuredClient);
        configuredClient.DefaultRequestHeaders.Remove(LocalApiKeyAuthenticationDefaults.HeaderName);

        using var request = CreateDeleteRequest(conversationId, "true");
        using var response = await configuredClient.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationDoesNotRevealAnotherPrincipalsConversation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        var databasePath = Path.Combine(directory.Path, "conversations.db");
        using var ownerClient = CreatePersistentIdentityClient(databasePath, "owner-a");
        var conversationId = await CreateConversationAsync(ownerClient);
        using var otherOwnerClient = CreatePersistentIdentityClient(databasePath, "owner-b");
        using var request = CreateDeleteRequest(conversationId, "true");

        using var response = await otherOwnerClient.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationReturnsNotFoundForAnUnknownConversation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var client = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a");
        using var request = CreateDeleteRequest(Guid.NewGuid(), "true");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationReturnsNotFoundForAnAnonymousConversation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        var databasePath = Path.Combine(directory.Path, "conversations.db");
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            ConfigurePersistentIdentity(builder, databasePath, "owner-a");
        });
        using var anonymousClient = factory.CreateClient();
        var conversationId = await CreateConversationAsync(anonymousClient);
        using var ownerClient = factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);
        using var request = CreateDeleteRequest(conversationId, "true");

        using var response = await ownerClient.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteConversationDoesNotCreateTheDatabaseWhenPersistenceIsDisabled()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        var databasePath = Path.Combine(directory.Path, "conversations.db");
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:ConversationPersistence:Enabled", "false");
            builder.UseSetting("LocalAssistant:ConversationPersistence:DatabasePath", databasePath);
        }).CreateClient();
        using var request = CreateDeleteRequest(Guid.NewGuid(), "true");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task DeleteConversationInvalidatesItsPendingConfirmation()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        using var client = CreatePersistentIdentityClient(
            Path.Combine(directory.Path, "conversations.db"),
            "owner-a",
            ["reminders.write"]);
        using var pendingResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Remind me to review the design", scenario = "reminder" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, pendingResponse.StatusCode);
        using var pendingBody = await JsonDocument.ParseAsync(
            await pendingResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = pendingBody.RootElement.GetProperty("conversationId").GetGuid();
        var confirmationId = pendingBody.RootElement
            .GetProperty("confirmation")
            .GetProperty("confirmationId")
            .GetGuid();

        using var deleteRequest = CreateDeleteRequest(conversationId, "true");
        using var deleteResponse = await client.SendAsync(deleteRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var decisionResponse = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/tool-confirmations/{confirmationId}/decisions",
            new { approved = true, scenario = "reminder" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, decisionResponse.StatusCode);
    }

    [Fact]
    public async Task ConfiguredIdentityKeepsPublicRequestsAvailableToAnonymousClients()
    {
        using var client = CreateIdentityClient(["time.read"]);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousClientCannotContinueAnAuthenticatedConversation()
    {
        using var client = CreateIdentityClient(["time.read"]);
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);

        using var ownerResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Owner message", scenario = "direct" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        using var ownerBody = await JsonDocument.ParseAsync(
            await ownerResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = ownerBody.RootElement.GetProperty("conversationId").GetGuid();

        using var continuedOwnerResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Owner follow-up", conversationId, scenario = "direct" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, continuedOwnerResponse.StatusCode);

        client.DefaultRequestHeaders.Remove(LocalApiKeyAuthenticationDefaults.HeaderName);
        using var anonymousResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Anonymous access", conversationId, scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, anonymousResponse.StatusCode);
        using var anonymousBody = await JsonDocument.ParseAsync(
            await anonymousResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        Assert.Equal(
            "conversation_not_found",
            anonymousBody.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnonymousClientCanContinueAnAnonymousConversation()
    {
        using var client = CreateIdentityClient(["time.read"]);

        using var firstResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Public message", scenario = "direct" },
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstBody = await JsonDocument.ParseAsync(
            await firstResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = firstBody.RootElement.GetProperty("conversationId").GetGuid();

        using var secondResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Public follow-up", conversationId, scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
    }

    [Fact]
    public async Task LegacyApiKeyHeaderIsRejectedBeforeDocumentResolution()
    {
        using var client = CreateIdentityClient(["time.read"]);
        client.DefaultRequestHeaders.Add(
            "X-LocalAssistant-Api-Key",
            "invalid-api-key");

        using var response = await client.GetAsync("/api/documents", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BootstrappedInstallationAuthenticatesItsOwnerWithAPrivateBearer()
    {
        using var stateDirectory = new TemporaryInstallationStateDirectory();
        var store = new FileInstallationIdentityStore(
            Options.Create(new InstallationIdentityOptions { StateDirectory = stateDirectory.Path }),
            TimeProvider.System);
        var bootstrap = await store.BootstrapAsync(CancellationToken.None);
        Assert.Equal(InstallationBootstrapStatus.Created, bootstrap.Status);

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("LocalAssistant:Installation:StateDirectory", stateDirectory.Path));
        using var client = factory.CreateClient();
        var session = await CreatePrivateBearerAsync(factory);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.Token);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Owner message", scenario = "direct" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScopedToolRequiresAConfiguredAuthenticatedPrincipal()
    {
        var tool = new ScopedCurrentTimeTool();
        using var anonymousClient = CreateIdentityClient(["time.read"], tool);
        using var anonymousResponse = await anonymousClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        using var noScopeClient = CreateIdentityClient([], tool);
        noScopeClient.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);
        using var noScopeResponse = await noScopeClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        using var authorizedClient = CreateIdentityClient(["time.read"], tool);
        authorizedClient.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);
        using var authorizedResponse = await authorizedClient.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noScopeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorizedResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmationEndpointExecutesOnlyThePendingToolCall()
    {
        var tool = new ConfirmationTemperatureTool();
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IToolRegistry>();
                services.AddSingleton<IToolRegistry>(_ => new ToolRegistry([tool]));
            });
        }).CreateClient();

        using var pendingResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Convert 100 Celsius to Fahrenheit", scenario = "temperature" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, pendingResponse.StatusCode);
        using var pendingBody = await JsonDocument.ParseAsync(
            await pendingResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = pendingBody.RootElement.GetProperty("conversationId").GetGuid();
        var confirmation = pendingBody.RootElement.GetProperty("confirmation");
        var confirmationId = confirmation.GetProperty("confirmationId").GetGuid();
        Assert.Equal("convert_temperature", confirmation.GetProperty("toolName").GetString());
        Assert.Equal(100, confirmation.GetProperty("arguments").GetProperty("value").GetInt32());
        Assert.Equal(0, tool.ExecutionCount);

        using var decisionResponse = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/tool-confirmations/{confirmationId}/decisions",
            new { approved = true, scenario = "temperature" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
        using var decisionBody = await JsonDocument.ParseAsync(
            await decisionResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        Assert.Equal("100 Celsius is 212 fahrenheit.", decisionBody.RootElement.GetProperty("content").GetString());
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task ReminderScenarioCreatesOneConfirmedTemporaryRecord()
    {
        using var client = CreateIdentityClient(["reminders.write"]);
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);

        using var pendingResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Remind me to review the design", scenario = "reminder" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, pendingResponse.StatusCode);
        using var pendingBody = await JsonDocument.ParseAsync(
            await pendingResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = pendingBody.RootElement.GetProperty("conversationId").GetGuid();
        var confirmation = pendingBody.RootElement.GetProperty("confirmation");
        var confirmationId = confirmation.GetProperty("confirmationId").GetGuid();
        Assert.Equal("create_reminder", confirmation.GetProperty("toolName").GetString());

        using var decisionResponse = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/tool-confirmations/{confirmationId}/decisions",
            new { approved = true, scenario = "reminder" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
        using var decisionBody = await JsonDocument.ParseAsync(
            await decisionResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        Assert.Equal(
            "Temporary reminder record created for experimental testing: Review the local reminder design. No notification has been scheduled.",
            decisionBody.RootElement.GetProperty("content").GetString());
        var tool = Assert.Single(decisionBody.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("create_reminder", tool.GetProperty("toolName").GetString());
        Assert.True(tool.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task BootstrapOwnerPrivateBearerCanCreateAConfirmedReminder()
    {
        using var factory = new LocalAssistantApiFactory();
        using var client = factory.CreateClient();
        var session = await CreatePrivateBearerAsync(factory);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.Token);

        using var pendingResponse = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Remind me to review the design", scenario = "reminder" },
            CancellationToken.None);
        using var pendingBody = await JsonDocument.ParseAsync(
            await pendingResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var conversationId = pendingBody.RootElement.GetProperty("conversationId").GetGuid();
        var confirmationId = pendingBody.RootElement
            .GetProperty("confirmation")
            .GetProperty("confirmationId")
            .GetGuid();

        using var decisionResponse = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/tool-confirmations/{confirmationId}/decisions",
            new { approved = true, scenario = "reminder" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, pendingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
    }

    [Fact]
    public async Task AnonymousClientCannotUseReminderScenario()
    {
        using var client = CreateIdentityClient(["reminders.write"]);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Remind me to review the design", scenario = "reminder" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PrincipalWithoutReminderScopeCannotUseReminderScenario()
    {
        using var client = CreateIdentityClient([]);
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Remind me to review the design", scenario = "reminder" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ToolFailureDoesNotExposeProviderContentToTheApiClient()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IToolRegistry>();
                services.AddSingleton<IToolRegistry>(_ => new ToolRegistry([new FailingCurrentTimeTool()]));
            });
        }).CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "What time is it?", scenario = "time" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.DoesNotContain("Sensitive provider detail", responseBody, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(responseBody);
        Assert.Equal("The time service could not complete the request.", body.RootElement
            .GetProperty("error")
            .GetProperty("message")
            .GetString());
    }

    [Fact]
    public async Task OllamaWithoutConfiguredModelReturnsValidationProblem()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", provider = "ollama" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OllamaModelWithoutToolsReturnsValidationProblem()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Ollama:Model", "test-model");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<OllamaModelInspector>()
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new StaticHttpMessageHandler(
                            """{ "capabilities": ["completion"] }"""));
            });
        }).CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Hello", provider = "ollama" },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal(
            "The configured Ollama model 'test-model' does not support tools.",
            Assert.Single(errors.GetProperty("provider").EnumerateArray()).GetString());
    }

    private HttpClient CreateIdentityClient(
        string[] scopes,
        ITool? replacementTool = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Identity:Enabled", "true");
            builder.UseSetting("LocalAssistant:Identity:PrincipalId", "test-owner");
            builder.UseSetting("LocalAssistant:Identity:ApiKeySha256", LocalApiKeyHash);
            for (var index = 0; index < scopes.Length; index++)
            {
                builder.UseSetting($"LocalAssistant:Identity:Scopes:{index}", scopes[index]);
            }

            if (replacementTool is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IToolRegistry>();
                    services.AddSingleton<IToolRegistry>(_ => new ToolRegistry([replacementTool]));
                });
            }
        }).CreateClient();
    }

    private HttpClient CreatePersistentIdentityClient(
        string databasePath,
        string principalId,
        string[]? scopes = null)
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            ConfigurePersistentIdentity(builder, databasePath, principalId, scopes);
        }).CreateClient();
        client.DefaultRequestHeaders.Add(
            LocalApiKeyAuthenticationDefaults.HeaderName,
            LocalApiKey);
        return client;
    }

    private static void ConfigurePersistentIdentity(
        IWebHostBuilder builder,
        string databasePath,
        string principalId,
        string[]? scopes = null)
    {
        builder.UseSetting("LocalAssistant:ConversationPersistence:Enabled", "true");
        builder.UseSetting("LocalAssistant:ConversationPersistence:DatabasePath", databasePath);
        builder.UseSetting("LocalAssistant:Identity:Enabled", "true");
        builder.UseSetting("LocalAssistant:Identity:PrincipalId", principalId);
        builder.UseSetting("LocalAssistant:Identity:ApiKeySha256", LocalApiKeyHash);

        if (scopes is null)
        {
            return;
        }

        for (var index = 0; index < scopes.Length; index++)
        {
            builder.UseSetting($"LocalAssistant:Identity:Scopes:{index}", scopes[index]);
        }
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/conversations/messages",
            new { message = "Private message", scenario = "direct" },
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        return body.RootElement.GetProperty("conversationId").GetGuid();
    }

    private static async Task<PrivateBearerSession> CreatePrivateBearerAsync(
        WebApplicationFactory<Program> factory,
        TimeSpan? lifetime = null)
    {
        var installation = factory.Services.GetRequiredService<IInstallationIdentityStore>();
        var installationIdentity = await installation.GetAsync(CancellationToken.None);
        if (installationIdentity is null)
        {
            var bootstrap = await installation.BootstrapAsync(CancellationToken.None);
            installationIdentity = await installation.GetAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("Installation bootstrap did not create an identity.");
        }

        var authentication = factory.Services.GetRequiredService<PrivateClientAuthenticationService>();
        var challenge = await authentication.CreateAdministrativeChallengeAsync(
            AdministrativeChallengeOperation.CreateClient,
            null,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var credential = await authentication.CompleteClientPairingAsync(
            challenge.Secret,
            installationIdentity.OwnerPrincipalId,
            "Test client",
            CancellationToken.None);
        var session = await authentication.CreateSessionAsync(
            credential!.Client.ClientId,
            credential.Secret,
            lifetime ?? TimeSpan.FromHours(1),
            CancellationToken.None);

        return new PrivateBearerSession(
            installationIdentity.OwnerPrincipalId,
            credential.Client.ClientId,
            session!.Session.SessionId,
            session.Token);
    }

    private static Task<HttpResponseMessage> SendBearerConversationAsync(
        HttpClient client,
        string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/messages")
        {
            Content = JsonContent.Create(new { message = "Hello", scenario = "direct" }),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, CancellationToken.None);
    }

    private sealed record PrivateBearerSession(
        string OwnerPrincipalId,
        string ClientId,
        string SessionId,
        string Token);

    private static HttpRequestMessage CreateDeleteRequest(
        Guid conversationId,
        params string?[] confirmationValues)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/conversations/{conversationId}");

        foreach (var confirmationValue in confirmationValues)
        {
            if (confirmationValue is not null)
            {
                request.Headers.Add("X-LocalAssistant-Confirm-Delete", confirmationValue);
            }
        }

        return request;
    }
}

public sealed class LocalAssistantApiFactory : WebApplicationFactory<Program>
{
    private readonly string _installationStateDirectory = Path.Combine(
        Path.GetTempPath(),
        $"LocalAssistant.ApiTests.{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "LocalAssistant:Installation:StateDirectory",
            _installationStateDirectory);
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestApiKeyAuthenticationDefaults.SchemeName;
                options.DefaultChallengeScheme = PrivateBearerAuthenticationDefaults.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestApiKeyAuthenticationHandler>(
                TestApiKeyAuthenticationDefaults.SchemeName,
                _ => { });
            services.AddSingleton<TimeProvider>(
                new ManualTimeProvider(new DateTimeOffset(2026, 8, 17, 14, 30, 0, TimeSpan.Zero)));
            services.AddSingleton<ILoopbackRequestPolicy, TestLoopbackRequestPolicy>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_installationStateDirectory))
        {
            Directory.Delete(_installationStateDirectory, recursive: true);
        }
    }
}

file sealed class TestLoopbackRequestPolicy : ILoopbackRequestPolicy
{
    public bool IsLoopback(Microsoft.AspNetCore.Http.HttpContext context) => true;
}

file sealed class RejectingLoopbackRequestPolicy : ILoopbackRequestPolicy
{
    public bool IsLoopback(Microsoft.AspNetCore.Http.HttpContext context) => false;
}

file sealed class ConfirmationTemperatureTool : ITool
{
    private readonly TemperatureConversionTool _inner = new();

    public ConfirmationTemperatureTool()
    {
        Definition = new ToolDefinition(
            new ToolMetadata(
                TemperatureConversionTool.ToolName,
                "Converts one temperature between supported units.",
                new ToolRiskProfile(
                    ToolOperationImpact.ChangesState,
                    ToolDataSensitivity.Public,
                    ToolExposure.Local,
                    ToolCost.None,
                    RequiresConfirmation: true,
                    [])),
            _inner.Definition.InputSchema);
    }

    public int ExecutionCount { get; private set; }

    public ToolDefinition Definition { get; }

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return await _inner.ExecuteAsync(arguments, cancellationToken);
    }
}

file sealed class ScopedCurrentTimeTool : ITool
{
    private readonly CurrentTimeTool _inner = new(TimeProvider.System);

    public ScopedCurrentTimeTool()
    {
        Definition = new ToolDefinition(
            new ToolMetadata(
                CurrentTimeTool.ToolName,
                "Reads the current time for an authorized principal.",
                new ToolRiskProfile(
                    ToolOperationImpact.ReadOnly,
                    ToolDataSensitivity.Private,
                    ToolExposure.Local,
                    ToolCost.None,
                    RequiresConfirmation: false,
                    ["time.read"])),
            _inner.Definition.InputSchema);
    }

    public ToolDefinition Definition { get; }

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) => _inner.ExecuteAsync(arguments, cancellationToken);
}

file sealed class FailingCurrentTimeTool : ITool
{
    private readonly CurrentTimeTool _inner = new(TimeProvider.System);

    public ToolDefinition Definition => _inner.Definition;

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionResult.Failure(
            "tool_execution_failed",
            "Sensitive provider detail",
            "The time service could not complete the request."));
}

file sealed class StaticHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseJson;

    public StaticHttpMessageHandler(string responseJson)
    {
        _responseJson = responseJson;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json"),
        });
    }
}
