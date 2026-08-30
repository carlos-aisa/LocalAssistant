using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Documents;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class HybridDocumentContentSearch : ILocalDocumentContentSearch, IDisposable
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
    };

    private readonly ILocalDocumentContentSearch _literalSearch;
    private readonly ILocalDocumentRoot _documentRoot;
    private readonly IDocumentReferenceProtector _documentReferenceProtector;
    private readonly IDocumentSemanticIndex _index;
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly DocumentSemanticSearchOptions _options;
    private readonly SemaphoreSlim _synchronizationLock = new(1, 1);

    public HybridDocumentContentSearch(
        ILocalDocumentContentSearch literalSearch,
        ILocalDocumentRoot documentRoot,
        IDocumentReferenceProtector documentReferenceProtector,
        IDocumentSemanticIndex index,
        ITextEmbeddingProvider embeddingProvider,
        DocumentSemanticSearchOptions? options = null)
    {
        _literalSearch = literalSearch;
        _documentRoot = documentRoot;
        _documentReferenceProtector = documentReferenceProtector;
        _index = index;
        _embeddingProvider = embeddingProvider;
        _options = options ?? new DocumentSemanticSearchOptions();
    }

    public async ValueTask<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        DocumentContentSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var literalResults = await _literalSearch.SearchAsync(query, cancellationToken);

        try
        {
            using var semanticTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            semanticTimeout.CancelAfter(_options.SynchronizationBudget);
            var queryEmbedding = await EmbedAsync(query.Text, semanticTimeout.Token);
            await SynchronizeAsync(queryEmbedding.Model, semanticTimeout.Token);

            var semanticResults = await SearchByEmbeddingAsync(
                query,
                queryEmbedding,
                cancellationToken);
            return Combine(literalResults, semanticResults, query.Limit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return literalResults;
        }
    }

    private async Task SynchronizeAsync(string embeddingModel, CancellationToken cancellationToken)
    {
        await _synchronizationLock.WaitAsync(cancellationToken);

        try
        {
            var rootPath = Path.GetFullPath(_documentRoot.Path);
            var indexedDocuments = await _index.GetDocumentsAsync(cancellationToken);
            var indexedByPath = indexedDocuments
                .Where(document => StringComparer.Ordinal.Equals(document.EmbeddingModel, embeddingModel))
                .ToDictionary(document => document.RelativePath, StringComparer.OrdinalIgnoreCase);
            var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var synchronizationCompleted = true;
            var processedFiles = 0;

            foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", EnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processedFiles >= _options.MaximumFilesPerSynchronizationCycle)
                {
                    synchronizationCompleted = false;
                    break;
                }

                var file = TryGetIndexableFile(rootPath, filePath);
                if (file is null)
                {
                    continue;
                }

                processedFiles++;

                var relativePath = Path.GetRelativePath(rootPath, file.FullName);
                discoveredPaths.Add(relativePath);
                var lastModifiedUtc = new DateTimeOffset(file.LastWriteTimeUtc);
                if (indexedByPath.TryGetValue(relativePath, out var indexed) &&
                    indexed.SizeBytes == file.Length &&
                    indexed.LastModifiedUtc == lastModifiedUtc)
                {
                    continue;
                }

                var text = await DocumentFilePolicy.ReadBoundedTextAsync(file.FullName, cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var chunks = new List<DocumentSemanticChunkInput>();
                foreach (var chunk in DocumentTextChunker.Split(text))
                {
                    var embedding = await EmbedAsync(chunk, cancellationToken);
                    chunks.Add(new DocumentSemanticChunkInput(chunk, embedding));
                }

                await _index.ReplaceAsync(
                    relativePath,
                    file.Length,
                    lastModifiedUtc,
                    chunks,
                    cancellationToken);
            }

            if (!synchronizationCompleted)
            {
                return;
            }

            foreach (var indexedDocument in indexedDocuments)
            {
                if (!discoveredPaths.Contains(indexedDocument.RelativePath))
                {
                    await _index.RemoveAsync(indexedDocument.RelativePath, cancellationToken);
                }
            }
        }
        finally
        {
            _synchronizationLock.Release();
        }
    }

    private async ValueTask<IReadOnlyList<ScoredDocument>> SearchByEmbeddingAsync(
        DocumentContentSearchQuery query,
        TextEmbedding queryEmbedding,
        CancellationToken cancellationToken)
    {
        var rootPath = Path.GetFullPath(_documentRoot.Path);
        var searchPath = DocumentFilePolicy.ResolveSearchPath(rootPath, query.RelativePath);
        if (searchPath is null)
        {
            return [];
        }

        var allowedRelativePath = Path.GetRelativePath(rootPath, searchPath);
        var chunks = await _index.GetChunksAsync(queryEmbedding.Model, cancellationToken);

        return chunks
            .Where(chunk => MatchesQuery(chunk, query, allowedRelativePath))
            .Select(chunk => new ScoredDocument(
                CreateResult(chunk),
                CalculateCosineSimilarity(queryEmbedding.Values, chunk.Embedding.Values)))
            .Where(result => result.Score is not null && result.Score >= _options.MinimumSimilarity)
            .Select(result => new ScoredDocument(result.Document, result.Score!.Value))
            .GroupBy(result => result.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Document.Excerpt is null)
                .ThenBy(result => result.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(query.Limit)
            .ToArray();
    }

    private static DocumentSearchResult[] Combine(
        IReadOnlyList<DocumentSearchResult> literalResults,
        IReadOnlyList<ScoredDocument> semanticResults,
        int limit)
    {
        return literalResults
            .Select(result => new ScoredDocument(result, 1))
            .Concat(semanticResults)
            .GroupBy(result => result.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(result => result.Document)
            .ToArray();
    }

    private DocumentSearchResult CreateResult(DocumentSemanticChunk chunk)
    {
        return new DocumentSearchResult(
            _documentReferenceProtector.Protect(chunk.RelativePath),
            Path.GetFileName(chunk.RelativePath),
            Path.GetExtension(chunk.RelativePath),
            chunk.RelativePath,
            chunk.SizeBytes,
            chunk.LastModifiedUtc,
            DocumentTextChunker.ToExcerpt(chunk.Text));
    }

    private static FileInfo? TryGetIndexableFile(string rootPath, string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!DocumentFilePolicy.IsAuthorizedFile(rootPath, fullPath))
            {
                return null;
            }

            var file = new FileInfo(fullPath);
            return DocumentFilePolicy.IsSupportedTextFormat(file) &&
                DocumentFilePolicy.IsWithinMaximumSize(file)
                ? file
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool MatchesQuery(
        DocumentSemanticChunk chunk,
        DocumentContentSearchQuery query,
        string allowedRelativePath)
    {
        if (!IsWithinSearchPath(chunk.RelativePath, allowedRelativePath))
        {
            return false;
        }

        var extension = Path.GetExtension(chunk.RelativePath);
        if (query.Extension is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(extension, query.Extension))
        {
            return false;
        }

        if (query.ModifiedAfterUtc is not null && chunk.LastModifiedUtc < query.ModifiedAfterUtc)
        {
            return false;
        }

        return query.ModifiedBeforeUtc is null || chunk.LastModifiedUtc <= query.ModifiedBeforeUtc;
    }

    private static bool IsWithinSearchPath(string relativePath, string allowedRelativePath)
    {
        if (allowedRelativePath == ".")
        {
            return true;
        }

        return relativePath.StartsWith(
            allowedRelativePath + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static double? CalculateCosineSimilarity(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        if (left.Count != right.Count)
        {
            return null;
        }

        double dotProduct = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var index = 0; index < left.Count; index++)
        {
            dotProduct += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return null;
        }

        return dotProduct / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private sealed record ScoredDocument(DocumentSearchResult Document, double? Score);

    private async ValueTask<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EmbeddingTimeout);
        return await _embeddingProvider.EmbedAsync(text, timeout.Token);
    }

    public void Dispose()
    {
        _synchronizationLock.Dispose();
    }
}
