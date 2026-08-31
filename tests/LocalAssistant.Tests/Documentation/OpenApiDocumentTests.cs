using Microsoft.OpenApi.Readers;

namespace LocalAssistant.Tests.Documentation;

public sealed class OpenApiDocumentTests
{
    [Fact]
    public void DocumentIsValidAndDescribesImplementedEndpoints()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "api", "openapi.yaml");
        using var stream = File.OpenRead(path);

        var document = new OpenApiStreamReader().Read(stream, out var diagnostic);

        Assert.Empty(diagnostic.Errors);
        Assert.Equal("0.1.0", document.Info.Version);
        Assert.Contains("/health", document.Paths.Keys);
        Assert.Contains("/api/documents", document.Paths.Keys);
        Assert.Contains("/api/documents/{id}/content", document.Paths.Keys);
        Assert.Contains("/api/conversations/messages", document.Paths.Keys);
        var privateBearer = Assert.IsType<Microsoft.OpenApi.Models.OpenApiSecurityScheme>(
            document.Components.SecuritySchemes["PrivateBearer"]);
        Assert.Equal("bearer", privateBearer.Scheme);
        Assert.False(document.Components.SecuritySchemes.ContainsKey("LocalApiKey"));
    }
}
