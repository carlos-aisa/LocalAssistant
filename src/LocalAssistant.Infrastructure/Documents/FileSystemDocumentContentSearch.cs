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
        var searchPath = DocumentFilePolicy.ResolveSearchPath(rootPath, query.RelativePath);
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

    private async ValueTask<DocumentSearchResult?> TryCreateResultAsync(
        string rootPath,
        string filePath,
        DocumentContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!DocumentFilePolicy.IsAuthorizedFile(rootPath, fullPath))
            {
                return null;
            }

            var file = new FileInfo(fullPath);
            var lastModifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc);
            if (!MatchesMetadata(file, lastModifiedUtc, query))
            {
                return null;
            }

            var text = await DocumentFilePolicy.ReadBoundedTextAsync(fullPath, cancellationToken);
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

    private static bool MatchesMetadata(
        FileInfo file,
        DateTimeOffset lastModifiedUtc,
        DocumentContentSearchQuery query)
    {
        if (!DocumentFilePolicy.IsSupportedTextFormat(file) ||
            !DocumentFilePolicy.IsWithinMaximumSize(file))
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
