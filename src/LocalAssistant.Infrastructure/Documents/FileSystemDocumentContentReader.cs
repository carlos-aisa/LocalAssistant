using LocalAssistant.Core.Documents;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class FileSystemDocumentContentReader : ILocalDocumentContentReader
{
    public const long MaximumFileSizeBytes = DocumentFilePolicy.MaximumFileSizeBytes;

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
            if (!DocumentFilePolicy.IsAuthorizedFile(rootPath, filePath))
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.NotFound);
            }

            var file = new FileInfo(filePath);
            if (!DocumentFilePolicy.IsSupportedTextFormat(file))
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.UnsupportedFormat);
            }

            if (!DocumentFilePolicy.IsWithinMaximumSize(file))
            {
                return DocumentContentReadOutcome.Failed(DocumentContentReadFailure.TooLarge);
            }

            var text = await DocumentFilePolicy.ReadBoundedTextAsync(filePath, cancellationToken);
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

}
