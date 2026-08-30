using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Documents;
using LocalAssistant.Infrastructure.Documents;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class HybridDocumentContentSearchTests
{
    [Fact]
    public async Task FindsAnIndexedDocumentWhenTheLiteralSearchHasNoMatch()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "dinner.md"), "Plan weekday family meals.");
        using var sut = CreateSearch(
            directory.Path,
            new StaticLiteralSearch([]),
            new MappingEmbeddingProvider());

        var results = await sut.SearchAsync(
            new DocumentContentSearchQuery("evening meal schedule"),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("dinner.md", result.RelativePath);
    }

    [Fact]
    public async Task ReturnsLiteralResultsWhenEmbeddingsAreUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var literalResult = new DocumentSearchResult(
            "note.md",
            "note.md",
            ".md",
            "note.md",
            10,
            new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        using var sut = CreateSearch(
            directory.Path,
            new StaticLiteralSearch([literalResult]),
            new FailingEmbeddingProvider());

        var results = await sut.SearchAsync(
            new DocumentContentSearchQuery("anything"),
            CancellationToken.None);

        Assert.Equal([literalResult], results);
    }

    private static HybridDocumentContentSearch CreateSearch(
        string documentsRoot,
        ILocalDocumentContentSearch literalSearch,
        ITextEmbeddingProvider embeddingProvider)
    {
        return new HybridDocumentContentSearch(
            literalSearch,
            new StaticLocalDocumentRoot(documentsRoot),
            new TestDocumentReferenceProtector(),
            new SqliteDocumentSemanticIndex(Path.Combine(documentsRoot, "semantic-index.db")),
            embeddingProvider);
    }

    private sealed class MappingEmbeddingProvider : ITextEmbeddingProvider
    {
        public ValueTask<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new TextEmbedding("test-model", [1f, 0f]));
        }
    }

    private sealed class FailingEmbeddingProvider : ITextEmbeddingProvider
    {
        public ValueTask<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Ollama is unavailable.");
        }
    }

    private sealed class StaticLiteralSearch(IReadOnlyList<DocumentSearchResult> results)
        : ILocalDocumentContentSearch
    {
        public ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
            DocumentContentSearchQuery query,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(results);
        }
    }

    private sealed class StaticLocalDocumentRoot(string path) : ILocalDocumentRoot
    {
        public string Path { get; } = path;
    }

    private sealed class TestDocumentReferenceProtector : IDocumentReferenceProtector
    {
        public string Protect(string relativePath) => relativePath;

        public bool TryUnprotect(string documentReference, out string relativePath)
        {
            relativePath = documentReference;
            return true;
        }
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
