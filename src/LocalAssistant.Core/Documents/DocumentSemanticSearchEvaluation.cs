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

public sealed record DocumentSearchEvaluationCase(string Id, string Query, string ExpectedDocumentId)
{
    public DocumentSearchEvaluationCase Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Query) || string.IsNullOrWhiteSpace(ExpectedDocumentId))
        {
            throw new ArgumentException("An evaluation case requires an id, query, and expected document id.");
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
            cases.Any(@case => !documents.Any(document => StringComparer.Ordinal.Equals(document.Id, @case.ExpectedDocumentId))))
        {
            throw new ArgumentException("The evaluation corpus identifiers are invalid.");
        }

        return this;
    }
}

public sealed record DocumentSearchEvaluationResult(string CaseId, int? ExpectedDocumentPosition, bool Hit, double ElapsedMilliseconds);

public sealed record DocumentSearchEvaluationReport(
    string CorpusVersion,
    string Strategy,
    string? EmbeddingModel,
    IReadOnlyList<DocumentSearchEvaluationResult> Results);

public sealed class DocumentSemanticSearchEvaluator(TimeProvider clock)
{
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
            return CreateResult(@case, ranked, clock.GetElapsedTime(start).TotalMilliseconds);
        }).ToArray();

        return new DocumentSearchEvaluationReport(corpus.Version, "literal", null, results);
    }

    public async ValueTask<DocumentSearchEvaluationReport> EvaluateSemanticAsync(
        DocumentSearchEvaluationCorpus corpus,
        int limit,
        ITextEmbeddingProvider embeddings,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        ArgumentNullException.ThrowIfNull(embeddings);
        corpus.Validate();
        var documentEmbeddings = new Dictionary<string, TextEmbedding>(StringComparer.Ordinal);
        foreach (var document in corpus.Documents)
        {
            documentEmbeddings.Add(document.Id, await embeddings.EmbedAsync(document.Content, cancellationToken));
        }

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
                .Select(pair => new { pair.Key, Score = CalculateCosineSimilarity(queryEmbedding.Values, pair.Value.Values) })
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Key, StringComparer.Ordinal)
                .Take(limit)
                .Select(result => result.Key)
                .ToArray();
            results.Add(CreateResult(@case, ranked, clock.GetElapsedTime(start).TotalMilliseconds));
        }

        return new DocumentSearchEvaluationReport(corpus.Version, "semantic", model, results);
    }

    private static DocumentSearchEvaluationResult CreateResult(DocumentSearchEvaluationCase @case, IReadOnlyList<string> ranked, double elapsedMilliseconds)
    {
        var position = Array.IndexOf(ranked.ToArray(), @case.ExpectedDocumentId) + 1;
        return new DocumentSearchEvaluationResult(@case.Id, position == 0 ? null : position, position > 0, elapsedMilliseconds);
    }

    private static void ValidateLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
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
