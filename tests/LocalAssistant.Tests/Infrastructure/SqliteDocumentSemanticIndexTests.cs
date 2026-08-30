using LocalAssistant.Core.Documents;
using LocalAssistant.Infrastructure.Documents;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class SqliteDocumentSemanticIndexTests
{
    [Fact]
    public async Task ReplacesExistingChunksAtomically()
    {
        using var directory = new TemporaryDirectory();
        var sut = new SqliteDocumentSemanticIndex(Path.Combine(directory.Path, "documents.db"));
        var modified = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

        await sut.ReplaceAsync("notes.md", 10, modified, ["first", "second"], CancellationToken.None);
        await sut.ReplaceAsync("notes.md", 20, modified.AddMinutes(1), ["replacement"], CancellationToken.None);

        var chunks = await sut.GetChunksAsync("notes.md", CancellationToken.None);

        var chunk = Assert.Single(chunks);
        Assert.Equal("replacement", chunk.Text);
        Assert.Equal(20, chunk.SizeBytes);
    }

    [Fact]
    public async Task RemovesAllChunksForADeletedDocument()
    {
        using var directory = new TemporaryDirectory();
        var sut = new SqliteDocumentSemanticIndex(Path.Combine(directory.Path, "documents.db"));
        var modified = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

        await sut.ReplaceAsync("notes.md", 10, modified, ["first"], CancellationToken.None);
        await sut.RemoveAsync("notes.md", CancellationToken.None);

        var chunks = await sut.GetChunksAsync("notes.md", CancellationToken.None);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ReturnsMetadataForEveryIndexedDocument()
    {
        using var directory = new TemporaryDirectory();
        var sut = new SqliteDocumentSemanticIndex(Path.Combine(directory.Path, "documents.db"));
        var firstModified = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        var secondModified = firstModified.AddMinutes(1);

        await sut.ReplaceAsync("first.md", 10, firstModified, ["first"], CancellationToken.None);
        await sut.ReplaceAsync("second.md", 20, secondModified, ["second"], CancellationToken.None);

        var documents = await sut.GetDocumentsAsync(CancellationToken.None);

        Assert.Collection(
            documents,
            first => Assert.Equal(new IndexedDocument("first.md", 10, firstModified), first),
            second => Assert.Equal(new IndexedDocument("second.md", 20, secondModified), second));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"LocalAssistant.Tests.{Guid.NewGuid():N}");
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
