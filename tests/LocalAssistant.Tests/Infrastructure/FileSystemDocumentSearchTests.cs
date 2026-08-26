using LocalAssistant.Core.Documents;
using LocalAssistant.Infrastructure.Documents;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class FileSystemDocumentSearchTests
{
    [Fact]
    public async Task SearchesOnlyMatchingMetadataWithinTheConfiguredRoot()
    {
        using var directory = new TemporaryDirectory();
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "nested"));
        File.WriteAllText(Path.Combine(directory.Path, "budget.txt"), "private content");
        File.WriteAllText(Path.Combine(directory.Path, "budget.md"), "private content");
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "budget.txt"), "private content");
        var search = new FileSystemDocumentSearch(new StaticLocalDocumentRoot(directory.Path));

        var results = await search.SearchAsync(
            new DocumentSearchQuery(name: "budget", extension: ".txt", relativePath: "nested"),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("budget.txt", result.Name);
        Assert.Equal(".txt", result.Extension);
        Assert.Equal(Path.Combine("nested", "budget.txt"), result.RelativePath);
        Assert.Equal(15, result.SizeBytes);
        Assert.DoesNotContain(directory.Path, result.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotSearchOutsideTheConfiguredRoot()
    {
        using var directory = new TemporaryDirectory();
        var search = new FileSystemDocumentSearch(new StaticLocalDocumentRoot(directory.Path));

        var results = await search.SearchAsync(
            new DocumentSearchQuery(relativePath: ".."),
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task LimitsTheNumberOfReturnedDocuments()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "first.txt"), "one");
        File.WriteAllText(Path.Combine(directory.Path, "second.txt"), "two");
        var search = new FileSystemDocumentSearch(new StaticLocalDocumentRoot(directory.Path));

        var results = await search.SearchAsync(
            new DocumentSearchQuery(extension: ".txt", limit: 1),
            CancellationToken.None);

        Assert.Single(results);
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
