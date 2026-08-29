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
    private readonly IDocumentReferenceProtector _documentReferenceProtector;

    public FileSystemDocumentSearch(
        ILocalDocumentRoot documentRoot,
        IDocumentReferenceProtector documentReferenceProtector)
    {
        _documentRoot = documentRoot;
        _documentReferenceProtector = documentReferenceProtector;
    }

    public ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rootPath = Path.GetFullPath(_documentRoot.Path);
        var searchPath = DocumentFilePolicy.ResolveSearchPath(rootPath, query.RelativePath);
        if (searchPath is null || !Directory.Exists(searchPath))
        {
            return ValueTask.FromResult<IReadOnlyList<DocumentSearchResult>>([]);
        }

        var results = new List<DocumentSearchResult>();
        foreach (var filePath in Directory.EnumerateFiles(searchPath, "*", EnumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryCreateResult(
                    rootPath,
                    filePath,
                    query,
                    _documentReferenceProtector,
                    out var result))
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

    private static bool TryCreateResult(
        string rootPath,
        string filePath,
        DocumentSearchQuery query,
        IDocumentReferenceProtector documentReferenceProtector,
        out DocumentSearchResult result)
    {
        result = default!;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!DocumentFilePolicy.IsAuthorizedFile(rootPath, fullPath))
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
                documentReferenceProtector.Protect(relativePath),
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

}
