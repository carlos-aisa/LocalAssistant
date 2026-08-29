using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Security;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Api;

public sealed class PersonalMemoryEndpointTests
{
    [Fact]
    public async Task AnonymousClientCannotCreatePersonalMemory()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.write"]);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/memories/personal",
            new CreatePersonalMemoryRequest("Prefers concise answers."),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadAndWriteScopesAreIndependent()
    {
        using var directory = new TemporaryDirectory();
        using var readFactory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.read"]);
        using var readClient = CreateAuthenticatedClient(readFactory, "owner-a-key");

        using var createResponse = await readClient.PostAsJsonAsync(
            "/api/memories/personal",
            new CreatePersonalMemoryRequest("Prefers concise answers."),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);

        using var writeFactory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.write"]);
        using var writeClient = CreateAuthenticatedClient(writeFactory, "owner-a-key");

        using var listResponse = await writeClient.GetAsync(
            "/api/memories/personal",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
    }

    [Fact]
    public async Task AuthorizedClientCanCreateAndListPersonalMemories()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.read", "memory.personal.write"]);
        using var client = CreateAuthenticatedClient(factory, "owner-a-key");

        using var createResponse = await client.PostAsJsonAsync(
            "/api/memories/personal",
            new CreatePersonalMemoryRequest("  Prefers lactose-free alternatives.  "),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(createResponse.Headers.Location);
        var createdPayload = await createResponse.Content.ReadAsStringAsync(
            CancellationToken.None);
        using var createdDocument = System.Text.Json.JsonDocument.Parse(createdPayload);
        Assert.False(createdDocument.RootElement.TryGetProperty("ownerPrincipalId", out _));
        Assert.Equal(
            "Prefers lactose-free alternatives.",
            createdDocument.RootElement.GetProperty("text").GetString());
        var createdId = createdDocument.RootElement.GetProperty("id").GetGuid();

        using var listResponse = await client.GetAsync(
            "/api/memories/personal?limit=1",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<PersonalMemoryListResponse>(
            cancellationToken: CancellationToken.None);
        Assert.NotNull(list);
        Assert.Equal(createdId, Assert.Single(list.Memories).Id);
        Assert.Equal("Prefers lactose-free alternatives.", list.Memories[0].Text);
    }

    [Fact]
    public async Task OtherPrincipalCannotDeletePersonalMemory()
    {
        using var directory = new TemporaryDirectory();
        using var ownerFactory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.read", "memory.personal.write"]);
        using var ownerClient = CreateAuthenticatedClient(ownerFactory, "owner-a-key");
        var memoryId = await CreateMemoryAsync(ownerClient, "Private preference.");

        using var otherFactory = CreateFactory(
            directory.DatabasePath,
            "owner-b",
            "owner-b-key",
            ["memory.personal.write"]);
        using var otherClient = CreateAuthenticatedClient(otherFactory, "owner-b-key");

        using var response = await otherClient.DeleteAsync(
            $"/api/memories/personal/{memoryId}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedClientCanDeleteItsOwnPersonalMemory()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.read", "memory.personal.write"]);
        using var client = CreateAuthenticatedClient(factory, "owner-a-key");
        var memoryId = await CreateMemoryAsync(client, "Temporary preference.");

        using var deleteResponse = await client.DeleteAsync(
            $"/api/memories/personal/{memoryId}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        using var listResponse = await client.GetAsync(
            "/api/memories/personal",
            CancellationToken.None);
        var list = await listResponse.Content.ReadFromJsonAsync<PersonalMemoryListResponse>(
            cancellationToken: CancellationToken.None);
        Assert.NotNull(list);
        Assert.Empty(list.Memories);
    }

    [Fact]
    public async Task PersonalMemoryEndpointsValidateInput()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.read", "memory.personal.write"]);
        using var client = CreateAuthenticatedClient(factory, "owner-a-key");

        using var createResponse = await client.PostAsJsonAsync(
            "/api/memories/personal",
            new CreatePersonalMemoryRequest(" "),
            CancellationToken.None);
        using var listResponse = await client.GetAsync(
            "/api/memories/personal?limit=0",
            CancellationToken.None);
        using var deleteResponse = await client.DeleteAsync(
            "/api/memories/personal/not-a-guid",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task DisabledPersistenceDoesNotCreateTheDatabaseFile()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(
            directory.DatabasePath,
            "owner-a",
            "owner-a-key",
            ["memory.personal.read", "memory.personal.write"],
            persistenceEnabled: false);
        using var client = CreateAuthenticatedClient(factory, "owner-a-key");

        using var response = await client.GetAsync(
            "/api/memories/personal",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(File.Exists(directory.DatabasePath));
    }

    [Fact]
    public async Task BootstrapOwnerCanCreateAndListPersonalMemories()
    {
        using var directory = new TemporaryDirectory();
        var stateDirectory = Path.Combine(directory.Path, "installation-state");
        var bootstrapStore = new FileInstallationIdentityStore(
            Options.Create(new InstallationIdentityOptions { StateDirectory = stateDirectory }),
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero)));
        var bootstrap = await bootstrapStore.BootstrapAsync(CancellationToken.None);
        using var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Installation:StateDirectory", stateDirectory);
            builder.UseSetting("LocalAssistant:ConversationPersistence:Enabled", "true");
            builder.UseSetting(
                "LocalAssistant:ConversationPersistence:DatabasePath",
                directory.DatabasePath);
        });
        using var client = CreateAuthenticatedClient(
            factory,
            Assert.IsType<string>(bootstrap.ApiKey));

        using var createResponse = await client.PostAsJsonAsync(
            "/api/memories/personal",
            new CreatePersonalMemoryRequest("Created by bootstrap."),
            CancellationToken.None);
        using var listResponse = await client.GetAsync(
            "/api/memories/personal",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<PersonalMemoryListResponse>(
            cancellationToken: CancellationToken.None);
        Assert.NotNull(list);
        Assert.Equal("Created by bootstrap.", Assert.Single(list.Memories).Text);
    }

    private static async Task<Guid> CreateMemoryAsync(HttpClient client, string text)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/memories/personal",
            new CreatePersonalMemoryRequest(text),
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var memory = await response.Content.ReadFromJsonAsync<PersonalMemoryResponse>(
            cancellationToken: CancellationToken.None);
        return Assert.IsType<PersonalMemoryResponse>(memory).Id;
    }

    private static HttpClient CreateAuthenticatedClient(
        WebApplicationFactory<Program> factory,
        string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, apiKey);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string databasePath,
        string principalId,
        string apiKey,
        string[] scopes,
        bool persistenceEnabled = true)
    {
        var apiKeyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        return new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Identity:Enabled", "true");
            builder.UseSetting("LocalAssistant:Identity:PrincipalId", principalId);
            builder.UseSetting("LocalAssistant:Identity:ApiKeySha256", apiKeyHash);
            builder.UseSetting(
                "LocalAssistant:ConversationPersistence:Enabled",
                persistenceEnabled.ToString());
            builder.UseSetting(
                "LocalAssistant:ConversationPersistence:DatabasePath",
                databasePath);
            for (var index = 0; index < scopes.Length; index++)
            {
                builder.UseSetting($"LocalAssistant:Identity:Scopes:{index}", scopes[index]);
            }
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalAssistant.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string DatabasePath => System.IO.Path.Combine(Path, "private-data.db");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
