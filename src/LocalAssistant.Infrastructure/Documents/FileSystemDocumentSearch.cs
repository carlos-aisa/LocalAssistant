using LocalAssistant.Core.Documents;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class FileSystemDocumentSearch : ILocalDocumentSearch
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
    };

    private readonly ILocalDocumentRoot _documentRoot;

    public FileSystemDocumentSearch(ILocalDocumentRoot documentRoot)
    {
        _documentRoot = documentRoot;
    }

    public ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rootPath = Path.GetFullPath(_documentRoot.Path);
        var searchPath = ResolveSearchPath(rootPath, query.RelativePath);
        if (searchPath is null || !Directory.Exists(searchPath))
        {
            return ValueTask.FromResult<IReadOnlyList<DocumentSearchResult>>([]);
        }

        var results = new List<DocumentSearchResult>();
        foreach (var filePath in Directory.EnumerateFiles(searchPath, "*", EnumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryCreateResult(rootPath, filePath, query, out var result))
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
        return ValueTask.FromResult<IReadOnlyList<DocumentSearchResult>>(results);
    }

    private static string? ResolveSearchPath(string rootPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return rootPath;
        }

        var path = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsWithinRoot(rootPath, path) || !Directory.Exists(path))
        {
            return null;
        }

        try
        {
            return ContainsReparsePoint(rootPath, path) ? null : path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryCreateResult(
        string rootPath,
        string filePath,
        DocumentSearchQuery query,
        out DocumentSearchResult result)
    {
        result = default!;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!IsWithinRoot(rootPath, fullPath) ||
                ContainsReparsePoint(rootPath, fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var file = new FileInfo(fullPath);
            var lastModifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc);
            if (!Matches(file, lastModifiedUtc, query))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(rootPath, fullPath);
            result = new DocumentSearchResult(
                Guid.NewGuid().ToString("N"),
                file.Name,
                file.Extension,
                relativePath,
                file.Length,
                lastModifiedUtc);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Matches(
        FileInfo file,
        DateTimeOffset lastModifiedUtc,
        DocumentSearchQuery query)
    {
        if (query.Name is not null &&
            !file.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase))
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
