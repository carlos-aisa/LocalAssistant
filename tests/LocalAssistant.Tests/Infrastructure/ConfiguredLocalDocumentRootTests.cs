using LocalAssistant.Infrastructure.Documents;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class ConfiguredLocalDocumentRootTests
{
    [Fact]
    public void UsesTheConfiguredExistingAbsoluteDirectory()
    {
        using var directory = new TemporaryDirectory();

        var root = CreateRoot(directory.Path, "unused");

        Assert.Equal(Path.GetFullPath(directory.Path), root.Path);
    }

    [Fact]
    public void UsesTheSystemDocumentsDirectoryWhenNoDirectoryIsConfigured()
    {
        using var directory = new TemporaryDirectory();

        var root = CreateRoot(null, directory.Path);

        Assert.Equal(Path.GetFullPath(directory.Path), root.Path);
    }

    [Fact]
    public void RejectsARelativeConfiguredDirectory()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateRoot("documents", "unused"));

        Assert.Equal("The documents root must be an absolute path.", exception.Message);
    }

    [Fact]
    public void RejectsAMissingConfiguredDirectory()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<DirectoryNotFoundException>(() =>
            CreateRoot(missingDirectory, "unused"));

        Assert.Equal("The configured documents root does not exist or is inaccessible.", exception.Message);
    }

    private static ConfiguredLocalDocumentRoot CreateRoot(
        string? configuredRoot,
        string systemDocumentsDirectory) => new(
        Options.Create(new LocalDocumentSourceOptions { DocumentsRoot = configuredRoot }),
        new FakeSystemDocumentsPathProvider(systemDocumentsDirectory));

    private sealed class FakeSystemDocumentsPathProvider(string path) : ISystemDocumentsPathProvider
    {
        public string GetPath() => path;
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
