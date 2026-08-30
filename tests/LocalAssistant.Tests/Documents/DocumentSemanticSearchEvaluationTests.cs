using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Documents;
using LocalAssistant.Tests.TestDoubles;

namespace LocalAssistant.Tests.Documents;

public sealed class DocumentSemanticSearchEvaluationTests
{
    private static readonly float[] OneDimensionEmbedding = [1f];
    private static readonly float[] TwoDimensionEmbedding = [1f, 0f];

    [Fact]
    public void LoadsTheSyntheticCorpusAndReportsLiteralMissesForParaphrasedQueries()
    {
        var corpus = LoadCorpus();
        var sut = new DocumentSemanticSearchEvaluator(
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero)));

        var report = sut.EvaluateLiteral(corpus, 2);

        Assert.Equal("literal", report.Strategy);
        Assert.All(
            report.Results.Where(result => result.ExpectsDocument),
            result => Assert.False(result.Hit));
        Assert.All(
            report.Results.Where(result => !result.ExpectsDocument),
            result => Assert.True(result.Hit));
        Assert.Null(report.EmbeddingModel);
    }

    [Fact]
    public async Task RanksSemanticMatchesAndDoesNotExposeQueriesOrDocumentContent()
    {
        var corpus = LoadCorpus();
        var sut = new DocumentSemanticSearchEvaluator(
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero)));
        var embeddings = new StaticEmbeddingProvider(new Dictionary<string, float[]>
        {
            ["Travel expenses for Lisbon included hotel, tram, and museum tickets."] = [1, 0],
            ["Water the vegetable garden every Tuesday morning."] = [0, 1],
            ["{ \"books\": [\"The Left Hand of Darkness\"] }"] = [-1, 0],
            ["day,dinner\nMonday,pasta\nTuesday,soup"] = [0, -1],
            ["Replace the worn chain and adjust the rear brake before cycling."] = [1, 1],
            ["The children return to class on 8 September after the summer break."] = [-1, -1],
            ["{ \"utility\": \"electricity\", \"amount\": 48.20 }"] = [1, -1],
            ["ingredient,quantity\nflour,500g\nyeast,7g"] = [-1, 1],
            ["Portugal holiday spending"] = [1, 0],
            ["when should I irrigate my plants"] = [0, 1],
            ["how do I fix my bike stopping safely"] = [1, 1],
            ["when do lessons resume after holidays"] = [-1, -1],
            ["how much was the power invoice"] = [1, -1],
            ["what do I need to make a loaf"] = [-1, 1],
            ["what time does the cinema open"] = [0, 0],
            ["how do I renew a passport"] = [0, 0],
        });

        var report = await sut.EvaluateSemanticAsync(corpus, 1, 0.78, embeddings, CancellationToken.None);
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);

        Assert.Equal("semantic", report.Strategy);
        Assert.NotNull(report.MinimumSimilarity);
        Assert.Equal(0.78, report.MinimumSimilarity.Value, 3);
        Assert.Equal(0, report.PreparationMilliseconds);
        Assert.All(report.Results, result => Assert.True(result.Hit));
        Assert.Equal(0, report.FalsePositiveCount);
        Assert.DoesNotContain("Lisbon", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Travel expenses", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsIncompatibleEmbeddingDimensions()
    {
        var corpus = LoadCorpus();
        var sut = new DocumentSemanticSearchEvaluator(TimeProvider.System);
        var embeddings = new StaticEmbeddingProvider(corpus.Documents.ToDictionary(
            document => document.Content,
            _ => OneDimensionEmbedding), TwoDimensionEmbedding);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sut.EvaluateSemanticAsync(corpus, 1, 0.78, embeddings, CancellationToken.None));
    }

    [Fact]
    public async Task ReportsAFalsePositiveWhenANegativeCaseExceedsTheMinimumSimilarity()
    {
        var corpus = CreateCorpus(
            new DocumentSearchEvaluationCase("unrelated", "unrelated question", null));
        var sut = new DocumentSemanticSearchEvaluator(TimeProvider.System);
        var embeddings = new StaticEmbeddingProvider(new Dictionary<string, float[]>
        {
            ["garden content"] = [1, 0],
            ["unrelated question"] = [1, 0],
        });

        var report = await sut.EvaluateSemanticAsync(corpus, 1, 0.78, embeddings, CancellationToken.None);
        var result = Assert.Single(report.Results);

        Assert.False(result.ExpectsDocument);
        Assert.True(result.ReturnedAnyDocument);
        Assert.Equal(1, result.TopScore);
        Assert.False(result.Hit);
        Assert.True(result.FalsePositive);
        Assert.Equal(1, report.FalsePositiveCount);
    }

    [Fact]
    public async Task RejectsAnExpectedDocumentBelowTheMinimumSimilarity()
    {
        var corpus = CreateCorpus(
            new DocumentSearchEvaluationCase("garden", "watering", "garden"));
        var sut = new DocumentSemanticSearchEvaluator(TimeProvider.System);
        var embeddings = new StaticEmbeddingProvider(new Dictionary<string, float[]>
        {
            ["garden content"] = [1, 0],
            ["watering"] = [0.5f, 0.8660254f],
        });

        var report = await sut.EvaluateSemanticAsync(corpus, 1, 0.78, embeddings, CancellationToken.None);
        var result = Assert.Single(report.Results);

        Assert.Equal(1, result.ExpectedDocumentPosition);
        Assert.NotNull(result.ExpectedDocumentScore);
        Assert.Equal(0.5, result.ExpectedDocumentScore.Value, 3);
        Assert.False(result.Hit);
        Assert.False(result.FalsePositive);
    }

    [Fact]
    public async Task RejectsAnOutOfRangeMinimumSimilarity()
    {
        var corpus = CreateCorpus(
            new DocumentSearchEvaluationCase("garden", "watering", "garden"));
        var sut = new DocumentSemanticSearchEvaluator(TimeProvider.System);
        var embeddings = new StaticEmbeddingProvider(new Dictionary<string, float[]>
        {
            ["garden content"] = [1, 0],
            ["watering"] = [1, 0],
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await sut.EvaluateSemanticAsync(corpus, 1, 1.01, embeddings, CancellationToken.None));
    }

    private static DocumentSearchEvaluationCorpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Documents", "Fixtures", "document-semantic-search-corpus.json");
        return DocumentSearchEvaluationCorpus.FromJson(File.ReadAllText(path));
    }

    private static DocumentSearchEvaluationCorpus CreateCorpus(params DocumentSearchEvaluationCase[] cases)
    {
        return new DocumentSearchEvaluationCorpus(
            "test",
            [new SyntheticDocument("garden", ".txt", "garden content")],
            cases).Validate();
    }

    private sealed class StaticEmbeddingProvider(
        IReadOnlyDictionary<string, float[]> embeddings,
        float[]? queryEmbedding = null) : ITextEmbeddingProvider
    {
        public ValueTask<TextEmbedding> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = embeddings.TryGetValue(text, out var embedding) ? embedding : queryEmbedding!;
            return ValueTask.FromResult(new TextEmbedding("test", values));
        }
    }
}
