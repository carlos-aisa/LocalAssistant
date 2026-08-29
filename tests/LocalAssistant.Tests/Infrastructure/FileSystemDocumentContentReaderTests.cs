using LocalAssistant.Core.Documents;
using LocalAssistant.Infrastructure.Documents;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class FileSystemDocumentContentReaderTests
{
    [Fact]
    public async Task ReadsSupportedTextFromTheConfiguredRoot()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "notes.md"), "# Private notes");
        var reader = CreateReader(directory.Path);

        var outcome = await reader.ReadAsync("notes.md", CancellationToken.None);

        var document = Assert.IsType<DocumentContent>(outcome.Document);
        Assert.Equal("notes.md", document.Name);
        Assert.Equal(".md", document.Extension);
        Assert.Equal("notes.md", document.RelativePath);
        Assert.Equal("# Private notes", document.Text);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public async Task DoesNotReadAPathOutsideTheConfiguredRoot()
    {
        using var directory = new TemporaryDirectory();
        var reader = CreateReader(directory.Path);

        var outcome = await reader.ReadAsync("..", CancellationToken.None);

        Assert.Null(outcome.Document);
        Assert.Equal(DocumentContentReadFailure.NotFound, outcome.Failure);
    }

    [Fact]
    public async Task RejectsUnsupportedFileFormats()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "notes.pdf"), "not a real PDF");
        var reader = CreateReader(directory.Path);

        var outcome = await reader.ReadAsync("notes.pdf", CancellationToken.None);

        Assert.Null(outcome.Document);
        Assert.Equal(DocumentContentReadFailure.UnsupportedFormat, outcome.Failure);
    }

    [Fact]
    public async Task RejectsFilesLargerThanTheMaximumSize()
    {
        using var directory = new TemporaryDirectory();
        var content = new byte[FileSystemDocumentContentReader.MaximumFileSizeBytes + 1];
        File.WriteAllBytes(Path.Combine(directory.Path, "large.txt"), content);
        var reader = CreateReader(directory.Path);

        var outcome = await reader.ReadAsync("large.txt", CancellationToken.None);

        Assert.Null(outcome.Document);
        Assert.Equal(DocumentContentReadFailure.TooLarge, outcome.Failure);
    }

    private static FileSystemDocumentContentReader CreateReader(string documentsRoot)
    {
        return new FileSystemDocumentContentReader(
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
            return documentReference != "invalid";
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
