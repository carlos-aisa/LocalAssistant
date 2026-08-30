using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Documents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LocalAssistant.Tests.Api;

public sealed class DocumentEndpointTests
{
    private const string ApiKey = "document-search-test-key";
    private static readonly string ApiKeyHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey)));

    [Fact]
    public async Task AnonymousClientCannotSearchDocuments()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.search"]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/documents", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedClientWithoutScopeCannotSearchDocuments()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, []);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync("/api/documents", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ContentSearchRequiresItsOwnScopeAndDoesNotReturnContent()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "notes.txt"), "private launch phrase");
        using var deniedFactory = CreateFactory(directory.Path, ["documents.search", "documents.read"]);
        using var deniedClient = deniedFactory.CreateClient();
        deniedClient.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var deniedResponse = await deniedClient.GetAsync(
            "/api/documents/content-search?text=launch%20phrase",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        using var allowedFactory = CreateFactory(directory.Path, ["documents.content.search"]);
        using var allowedClient = allowedFactory.CreateClient();
        allowedClient.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);
        using var allowedResponse = await allowedClient.GetAsync(
            "/api/documents/content-search?text=LAUNCH%20PHRASE",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await allowedResponse.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var document = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal("notes.txt", document.GetProperty("name").GetString());
        Assert.True(document.GetProperty("excerpt").ValueKind == JsonValueKind.Null);
        Assert.False(document.TryGetProperty("text", out _));
        Assert.False(document.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task ContentSearchIncludesAnExcerptOnlyWhenReadScopeIsAlsoGranted()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "notes.txt"), "private launch phrase");
        using var factory = CreateFactory(
            directory.Path,
            ["documents.content.search", "documents.read"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync(
            "/api/documents/content-search?text=launch%20phrase",
            CancellationToken.None);

        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var document = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal("private launch phrase", document.GetProperty("excerpt").GetString());
    }

    [Fact]
    public async Task AnonymousClientCannotSearchDocumentContent()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.content.search"]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/documents/content-search?text=match",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ContentSearchRejectsAnInvalidQuery()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.content.search"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync(
            "/api/documents/content-search?text=%20",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ContentSearchRejectsAnAbsoluteSearchPath()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.content.search"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);
        var absolutePath = Uri.EscapeDataString(Path.GetTempPath());

        using var response = await client.GetAsync(
            $"/api/documents/content-search?text=match&relativePath={absolutePath}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedClientReceivesMetadataWithoutContentOrAbsolutePaths()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "budget.txt"), "private content");
        using var factory = CreateFactory(directory.Path, ["documents.search"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync(
            "/api/documents?name=budget&extension=.txt",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var document = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal("budget.txt", document.GetProperty("name").GetString());
        Assert.Equal(".txt", document.GetProperty("extension").GetString());
        Assert.Equal("budget.txt", document.GetProperty("relativePath").GetString());
        Assert.False(document.TryGetProperty("content", out _));
        Assert.DoesNotContain(directory.Path, document.GetProperty("relativePath").GetString());
    }

    [Fact]
    public async Task AuthorizedClientCannotUseAnAbsoluteSearchPath()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.search"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);
        var absolutePath = Uri.EscapeDataString(Path.GetTempPath());

        using var response = await client.GetAsync(
            $"/api/documents?relativePath={absolutePath}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchScopeAloneCannotReadDocumentContent()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.search"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        using var response = await client.GetAsync(
            "/api/documents/invalid/content",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousClientCannotReadDocumentContent()
    {
        using var directory = new TemporaryDirectory();
        using var factory = CreateFactory(directory.Path, ["documents.read"]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/documents/invalid/content",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedClientCanReadContentUsingAReferenceFromSearch()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "budget.txt"), "private content");
        using var factory = CreateFactory(directory.Path, ["documents.search", "documents.read"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        var documentReference = await SearchForDocumentReferenceAsync(client);
        using var response = await client.GetAsync(
            $"/api/documents/{Uri.EscapeDataString(documentReference)}/content",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        Assert.Equal("budget.txt", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("private content", body.RootElement.GetProperty("text").GetString());
        Assert.False(body.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task AlteredDocumentReferenceCannotBeRead()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "budget.txt"), "private content");
        using var factory = CreateFactory(directory.Path, ["documents.search", "documents.read"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        var documentReference = await SearchForDocumentReferenceAsync(client);
        using var response = await client.GetAsync(
            $"/api/documents/{Uri.EscapeDataString(documentReference)}x/content",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedDocumentFormatReturnsUnprocessableContent()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "budget.pdf"), "not a real PDF");
        using var factory = CreateFactory(directory.Path, ["documents.search", "documents.read"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(LocalApiKeyAuthenticationDefaults.HeaderName, ApiKey);

        var documentReference = await SearchForDocumentReferenceAsync(client);
        using var response = await client.GetAsync(
            $"/api/documents/{Uri.EscapeDataString(documentReference)}/content",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private static async Task<string> SearchForDocumentReferenceAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/documents", CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(CancellationToken.None),
            cancellationToken: CancellationToken.None);
        var document = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray());
        return Assert.IsType<string>(document.GetProperty("id").GetString());
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string documentsRoot,
        string[] scopes)
    {
        var factory = new LocalAssistantApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("LocalAssistant:Identity:Enabled", "true");
            builder.UseSetting("LocalAssistant:Identity:PrincipalId", "document-owner");
            builder.UseSetting("LocalAssistant:Identity:ApiKeySha256", ApiKeyHash);
            for (var index = 0; index < scopes.Length; index++)
            {
                builder.UseSetting($"LocalAssistant:Identity:Scopes:{index}", scopes[index]);
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILocalDocumentRoot>();
                services.AddSingleton<ILocalDocumentRoot>(new StaticLocalDocumentRoot(documentsRoot));
            });
        });

        return factory;
    }

    private sealed class StaticLocalDocumentRoot(string path) : ILocalDocumentRoot
    {
        public string Path { get; } = path;
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

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
