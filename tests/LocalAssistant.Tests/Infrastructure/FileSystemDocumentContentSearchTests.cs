using LocalAssistant.Core.Documents;
using LocalAssistant.Infrastructure.Documents;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class FileSystemDocumentContentSearchTests
{
    [Fact]
    public async Task FindsCaseInsensitiveTextOnlyInSupportedDocuments()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "notes.md"), "Private launch plan");
        File.WriteAllText(Path.Combine(directory.Path, "image.pdf"), "launch plan");
        var search = CreateSearch(directory.Path);

        var results = await search.SearchAsync(
            new DocumentContentSearchQuery("LAUNCH PLAN"),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("notes.md", result.Name);
        Assert.Equal("notes.md", result.RelativePath);
    }

    [Fact]
    public async Task SkipsDocumentsLargerThanTheMaximumSize()
    {
        using var directory = new TemporaryDirectory();
        var content = new byte[FileSystemDocumentContentReader.MaximumFileSizeBytes + 1];
        File.WriteAllBytes(Path.Combine(directory.Path, "large.txt"), content);
        var search = CreateSearch(directory.Path);

        var results = await search.SearchAsync(
            new DocumentContentSearchQuery("anything"),
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DoesNotSearchOutsideTheConfiguredRoot()
    {
        using var directory = new TemporaryDirectory();
        var search = CreateSearch(directory.Path);

        var results = await search.SearchAsync(
            new DocumentContentSearchQuery("anything", relativePath: ".."),
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task AppliesMetadataFiltersAndTheResultLimit()
    {
        using var directory = new TemporaryDirectory();
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "first.txt"), "match");
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "second.md"), "match");
        var search = CreateSearch(directory.Path);

        var results = await search.SearchAsync(
            new DocumentContentSearchQuery(
                "match",
                extension: ".txt",
                relativePath: "nested",
                limit: 1),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("first.txt", result.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsBlankSearchText(string text)
    {
        Assert.Throws<ArgumentException>(() => new DocumentContentSearchQuery(text));
    }

    [Fact]
    public void RejectsAnInvalidDateRange()
    {
        var after = DateTimeOffset.UtcNow;
        var before = after.AddMinutes(-1);

        Assert.Throws<ArgumentException>(() => new DocumentContentSearchQuery(
            "match",
            modifiedAfterUtc: after,
            modifiedBeforeUtc: before));
    }

    private static FileSystemDocumentContentSearch CreateSearch(string documentsRoot)
    {
        return new FileSystemDocumentContentSearch(
            new StaticLocalDocumentRoot(documentsRoot),
            new TestDocumentReferenceProtector());
    }

    private sealed class StaticLocalDocumentRoot(string path) : ILocalDocumentRoot
    {
        public string Path { get; } = path;
    }

    private sealed class TestDocumentReferenceProtector : IDocumentReferenceProtector
    {
        public string Protect(string relativePath)
        {
            return relativePath;
        }

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
