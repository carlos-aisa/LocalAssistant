using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Documents;
using LocalAssistant.Tests.TestDoubles;

namespace LocalAssistant.Tests.Documents;

public sealed class DocumentSemanticSearchEvaluationTests
{
    private static readonly float[] OneDimensionEmbedding = [1f];
    private static readonly float[] TwoDimensionEmbedding = [1f, 0f];

    [Fact]
    public void LoadsTheSyntheticCorpusAndRanksLiteralMatches()
    {
        var corpus = LoadCorpus();
        var sut = new DocumentSemanticSearchEvaluator(
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero)));

        var report = sut.EvaluateLiteral(corpus, 2);

        Assert.Equal("literal", report.Strategy);
        Assert.All(report.Results, result => Assert.True(result.Hit));
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
            ["Lisbon"] = [1, 0],
            ["vegetable garden"] = [0, 1],
        });

        var report = await sut.EvaluateSemanticAsync(corpus, 1, embeddings, CancellationToken.None);
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);

        Assert.Equal("semantic", report.Strategy);
        Assert.All(report.Results, result => Assert.True(result.Hit));
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
            await sut.EvaluateSemanticAsync(corpus, 1, embeddings, CancellationToken.None));
    }

    private static DocumentSearchEvaluationCorpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Documents", "Fixtures", "document-semantic-search-corpus.json");
        return DocumentSearchEvaluationCorpus.FromJson(File.ReadAllText(path));
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
