using System.Text;
using LocalAssistant.Core.Documents;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class FileSystemDocumentContentReader : ILocalDocumentContentReader
{
    public const long MaximumFileSizeBytes = 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".json",
        ".md",
        ".txt",
    };

    private readonly ILocalDocumentRoot _documentRoot;
    private readonly IDocumentReferenceProtector _documentReferenceProtector;

    public FileSystemDocumentContentReader(
        ILocalDocumentRoot documentRoot,
        IDocumentReferenceProtector documentReferenceProtector)
    {
        _documentRoot = documentRoot;
        _documentReferenceProtector = documentReferenceProtector;
    }

    public async ValueTask<DocumentContentReadOutcome> ReadAsync(
        string documentReference,
        CancellationToken cancellationToken)
    {
        if (!_documentReferenceProtector.TryUnprotect(documentReference, out var relativePath))
        {
            return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.NotFound);
        }

        try
        {
            var rootPath = Path.GetFullPath(_documentRoot.Path);
            var filePath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            if (!IsAuthorizedFile(rootPath, filePath))
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.NotFound);
            }

            var file = new FileInfo(filePath);
            if (!SupportedExtensions.Contains(file.Extension))
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.UnsupportedFormat);
            }

            if (file.Length > MaximumFileSizeBytes)
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.TooLarge);
            }

            var text = await ReadBoundedTextAsync(filePath, cancellationToken);
            if (text is null)
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.TooLarge);
            }

            var document = new DocumentContent(
                file.Name,
                file.Extension,
                Path.GetRelativePath(rootPath, file.FullName),
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc),
                text);
            return DocumentContentReadOutcome.Found(document);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.NotFound);
        }
    }

    private static bool IsAuthorizedFile(string rootPath, string filePath)
    {
        return IsWithinRoot(rootPath, filePath) &&
            File.Exists(filePath) &&
            !ContainsReparsePoint(rootPath, filePath) &&
            (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) == 0;
    }

    private static async ValueTask<string?> ReadBoundedTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];

        while (true)
        {
            var bytesRead = await fileStream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (buffer.Length + bytesRead > MaximumFileSizeBytes)
            {
                return null;
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, bytesRead), cancellationToken);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            StringComparer.OrdinalIgnoreCase.Equals(rootPath, candidatePath);
    }

    private static bool ContainsReparsePoint(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        var currentPath = rootPath;

        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment == ".")
            {
                continue;
            }

            currentPath = Path.Combine(currentPath, segment);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
