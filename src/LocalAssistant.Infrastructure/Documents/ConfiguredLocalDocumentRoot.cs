using LocalAssistant.Core.Documents;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class LocalDocumentSourceOptions
{
    public const string SectionName = "LocalAssistant:DocumentSources";

    public string? DocumentsRoot { get; set; }
}

public interface ISystemDocumentsPathProvider
{
    string GetPath();
}

public sealed class SystemDocumentsPathProvider : ISystemDocumentsPathProvider
{
    public string GetPath() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}

public sealed class ConfiguredLocalDocumentRoot : ILocalDocumentRoot
{
    public ConfiguredLocalDocumentRoot(
        IOptions<LocalDocumentSourceOptions> options,
        ISystemDocumentsPathProvider systemDocumentsPathProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(systemDocumentsPathProvider);

        var configuredRoot = options.Value.DocumentsRoot;
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? systemDocumentsPathProvider.GetPath()
            : configuredRoot;

        if (!System.IO.Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException("The documents root must be an absolute path.");
        }

        Path = System.IO.Path.GetFullPath(root);
        if (!Directory.Exists(Path))
        {
            throw new DirectoryNotFoundException("The configured documents root does not exist or is inaccessible.");
        }
    }

    public string Path { get; }
}
