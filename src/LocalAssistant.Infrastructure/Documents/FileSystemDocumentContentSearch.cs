using System.Text;
using LocalAssistant.Core.Documents;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class FileSystemDocumentContentSearch : ILocalDocumentContentSearch
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".json",
        ".md",
        ".txt",
    };

    private readonly ILocalDocumentRoot _documentRoot;
    private readonly IDocumentReferenceProtector _documentReferenceProtector;

    public FileSystemDocumentContentSearch(
        ILocalDocumentRoot documentRoot,
        IDocumentReferenceProtector documentReferenceProtector)
    {
        _documentRoot = documentRoot;
        _documentReferenceProtector = documentReferenceProtector;
    }

    public async ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rootPath = Path.GetFullPath(_documentRoot.Path);
        var searchPath = ResolveSearchPath(rootPath, query.RelativePath);
        if (searchPath is null || !Directory.Exists(searchPath))
        {
            return [];
        }

        var results = new List<DocumentSearchResult>();
        foreach (var filePath in Directory.EnumerateFiles(searchPath, "*", EnumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryCreateResultAsync(
                rootPath,
                filePath,
                query,
                cancellationToken);
            if (result is null)
            {
                continue;
            }

            results.Add(result);
            if (results.Count == query.Limit)
            {
                break;
            }
        }

        results.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            left.RelativePath,
            right.RelativePath));
        return results;
    }

    private static string? ResolveSearchPath(string rootPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return rootPath;
        }

        var searchPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsWithinRoot(rootPath, searchPath) || !Directory.Exists(searchPath))
        {
            return null;
        }

        try
        {
            return ContainsReparsePoint(rootPath, searchPath) ? null : searchPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async ValueTask<DocumentSearchResult?> TryCreateResultAsync(
        string rootPath,
        string filePath,
        DocumentContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!IsAuthorizedFile(rootPath, fullPath))
            {
                return null;
            }

            var file = new FileInfo(fullPath);
            var lastModifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc);
            if (!MatchesMetadata(file, lastModifiedUtc, query))
            {
                return null;
            }

            var text = await ReadBoundedTextAsync(fullPath, cancellationToken);
            if (text is null ||
                !text.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            return new DocumentSearchResult(
                _documentReferenceProtector.Protect(relativePath),
                file.Name,
                file.Extension,
                relativePath,
                file.Length,
                lastModifiedUtc);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            DecoderFallbackException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsAuthorizedFile(string rootPath, string filePath)
    {
        return IsWithinRoot(rootPath, filePath) &&
            File.Exists(filePath) &&
            !ContainsReparsePoint(rootPath, filePath) &&
            (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) == 0;
    }

    private static bool MatchesMetadata(
        FileInfo file,
        DateTimeOffset lastModifiedUtc,
        DocumentContentSearchQuery query)
    {
        if (!SupportedExtensions.Contains(file.Extension) ||
            file.Length > FileSystemDocumentContentReader.MaximumFileSizeBytes)
        {
            return false;
        }

        if (query.Extension is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(file.Extension, query.Extension))
        {
            return false;
        }

        if (query.ModifiedAfterUtc is not null && lastModifiedUtc < query.ModifiedAfterUtc)
        {
            return false;
        }

        return query.ModifiedBeforeUtc is null || lastModifiedUtc <= query.ModifiedBeforeUtc;
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

            if (buffer.Length + bytesRead > FileSystemDocumentContentReader.MaximumFileSizeBytes)
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

        foreach (var segment in relativePath.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
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
