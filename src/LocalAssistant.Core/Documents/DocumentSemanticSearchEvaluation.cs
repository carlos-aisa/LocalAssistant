using System.Text.Json;
using LocalAssistant.Core.Conversations;

namespace LocalAssistant.Core.Documents;

public sealed record SyntheticDocument(string Id, string Format, string Content)
{
    public SyntheticDocument Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Format) || string.IsNullOrWhiteSpace(Content))
        {
            throw new ArgumentException("A synthetic document requires an id, format, and content.");
        }

        return this;
    }
}

public sealed record DocumentSearchEvaluationCase(string Id, string Query, string? ExpectedDocumentId)
{
    public DocumentSearchEvaluationCase Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Query))
        {
            throw new ArgumentException("An evaluation case requires an id and query.");
        }

        return this;
    }
}

public sealed record DocumentSearchEvaluationCorpus(
    string Version,
    IReadOnlyList<SyntheticDocument> Documents,
    IReadOnlyList<DocumentSearchEvaluationCase> Cases)
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static DocumentSearchEvaluationCorpus FromJson(string json)
    {
        var corpus = JsonSerializer.Deserialize<DocumentSearchEvaluationCorpus>(
            json,
            SerializerOptions)
            ?? throw new ArgumentException("The evaluation corpus is invalid.", nameof(json));
        return corpus.Validate();
    }

    public DocumentSearchEvaluationCorpus Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Documents.Count == 0 || Cases.Count == 0)
        {
            throw new ArgumentException("The evaluation corpus is incomplete.");
        }

        var documents = Documents.Select(document => document.Validate()).ToArray();
        var cases = Cases.Select(@case => @case.Validate()).ToArray();
        if (documents.Select(document => document.Id).Distinct(StringComparer.Ordinal).Count() != documents.Length ||
            cases.Select(@case => @case.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length ||
            cases
                .Where(@case => @case.ExpectedDocumentId is not null)
                .Any(@case => !documents.Any(document => StringComparer.Ordinal.Equals(document.Id, @case.ExpectedDocumentId))))
        {
            throw new ArgumentException("The evaluation corpus identifiers are invalid.");
        }

        return this;
    }
}

public sealed record DocumentSearchEvaluationResult(
    string CaseId,
    bool ExpectsDocument,
    bool ReturnedAnyDocument,
    int? ExpectedDocumentPosition,
    double? ExpectedDocumentScore,
    double? TopScore,
    bool Hit,
    bool FalsePositive,
    double ElapsedMilliseconds);

public sealed record DocumentSearchEvaluationReport(
    string CorpusVersion,
    string Strategy,
    string? EmbeddingModel,
    double? MinimumSimilarity,
    double PreparationMilliseconds,
    IReadOnlyList<DocumentSearchEvaluationResult> Results)
{
    public int HitCount => Results.Count(result => result.Hit);

    public int FailureCount => Results.Count(result => !result.Hit);

    public int FalsePositiveCount => Results.Count(result => result.FalsePositive);
}

public sealed class DocumentSemanticSearchEvaluator(TimeProvider clock)
{
    private sealed record RankedDocument(string Id, double Score);

    public DocumentSearchEvaluationReport EvaluateLiteral(DocumentSearchEvaluationCorpus corpus, int limit)
    {
        ValidateLimit(limit);
        corpus.Validate();
        var results = corpus.Cases.Select(@case =>
        {
            var start = clock.GetTimestamp();
            var ranked = corpus.Documents
                .Where(document => document.Content.Contains(@case.Query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(document => document.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(document => document.Id)
                .ToArray();
            return CreateLiteralResult(@case, ranked, clock.GetElapsedTime(start).TotalMilliseconds);
        }).ToArray();

        return new DocumentSearchEvaluationReport(corpus.Version, "literal", null, null, 0, results);
    }

    public async ValueTask<DocumentSearchEvaluationReport> EvaluateSemanticAsync(
        DocumentSearchEvaluationCorpus corpus,
        int limit,
        double minimumSimilarity,
        ITextEmbeddingProvider embeddings,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        ValidateMinimumSimilarity(minimumSimilarity);
        ArgumentNullException.ThrowIfNull(embeddings);
        corpus.Validate();
        var documentEmbeddings = new Dictionary<string, TextEmbedding>(StringComparer.Ordinal);
        var preparationStart = clock.GetTimestamp();
        foreach (var document in corpus.Documents)
        {
            documentEmbeddings.Add(document.Id, await embeddings.EmbedAsync(document.Content, cancellationToken));
        }

        var preparationMilliseconds = clock.GetElapsedTime(preparationStart).TotalMilliseconds;
        var model = documentEmbeddings.Values.First().Model;
        var results = new List<DocumentSearchEvaluationResult>();
        foreach (var @case in corpus.Cases)
        {
            var start = clock.GetTimestamp();
            var queryEmbedding = await embeddings.EmbedAsync(@case.Query, cancellationToken);
            if (!StringComparer.Ordinal.Equals(model, queryEmbedding.Model))
            {
                throw new InvalidOperationException("The embedding model changed during evaluation.");
            }

            var ranked = documentEmbeddings
                .Select(pair => new RankedDocument(
                    pair.Key,
                    CalculateCosineSimilarity(queryEmbedding.Values, pair.Value.Values)))
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Id, StringComparer.Ordinal)
                .ToArray();
            results.Add(CreateSemanticResult(
                @case,
                ranked,
                limit,
                minimumSimilarity,
                clock.GetElapsedTime(start).TotalMilliseconds));
        }

        return new DocumentSearchEvaluationReport(
            corpus.Version,
            "semantic",
            model,
            minimumSimilarity,
            preparationMilliseconds,
            results);
    }

    private static DocumentSearchEvaluationResult CreateLiteralResult(
        DocumentSearchEvaluationCase @case,
        IReadOnlyList<string> ranked,
        double elapsedMilliseconds)
    {
        var returnedAnyDocument = ranked.Count > 0;
        if (@case.ExpectedDocumentId is null)
        {
            return new DocumentSearchEvaluationResult(
                @case.Id,
                false,
                returnedAnyDocument,
                null,
                null,
                null,
                !returnedAnyDocument,
                false,
                elapsedMilliseconds);
        }

        var position = Array.IndexOf(ranked.ToArray(), @case.ExpectedDocumentId) + 1;
        return new DocumentSearchEvaluationResult(
            @case.Id,
            true,
            returnedAnyDocument,
            position == 0 ? null : position,
            null,
            null,
            position > 0,
            false,
            elapsedMilliseconds);
    }

    private static DocumentSearchEvaluationResult CreateSemanticResult(
        DocumentSearchEvaluationCase @case,
        IReadOnlyList<RankedDocument> ranked,
        int limit,
        double minimumSimilarity,
        double elapsedMilliseconds)
    {
        var limitedRanked = ranked.Take(limit).ToArray();
        var returnedAnyDocument = limitedRanked.Length > 0;
        double? topScore = returnedAnyDocument ? limitedRanked[0].Score : null;
        if (@case.ExpectedDocumentId is null)
        {
            var falsePositive = topScore.HasValue && topScore.Value >= minimumSimilarity;
            return new DocumentSearchEvaluationResult(
                @case.Id,
                false,
                returnedAnyDocument,
                null,
                null,
                topScore,
                !falsePositive,
                falsePositive,
                elapsedMilliseconds);
        }

        var expectedRank = ranked
            .Select((result, index) => new { Result = result, Position = index + 1 })
            .Single(result => StringComparer.Ordinal.Equals(result.Result.Id, @case.ExpectedDocumentId));
        int? expectedPosition = expectedRank.Position <= limit ? expectedRank.Position : null;
        var expectedScore = expectedRank.Result.Score;
        var hit = expectedPosition is not null && expectedScore >= minimumSimilarity;
        return new DocumentSearchEvaluationResult(
            @case.Id,
            true,
            returnedAnyDocument,
            expectedPosition,
            expectedScore,
            topScore,
            hit,
            false,
            elapsedMilliseconds);
    }

    private static void ValidateLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
    }

    private static void ValidateMinimumSimilarity(double minimumSimilarity)
    {
        if (minimumSimilarity is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSimilarity));
        }
    }

    private static double CalculateCosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count)
        {
            throw new ArgumentException("Embedding dimensions must match.");
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

        return leftMagnitude == 0 || rightMagnitude == 0 ? 0 : dotProduct / Math.Sqrt(leftMagnitude * rightMagnitude);
    }
}
