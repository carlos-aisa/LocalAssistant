namespace LocalAssistant.Infrastructure.Documents;

public sealed class DocumentSemanticSearchOptions
{
    public const string SectionName = "LocalAssistant:DocumentSemanticSearch";

    public double MinimumSimilarity { get; set; } = 0.78;

    public int MaximumFilesPerSynchronizationCycle { get; set; } = 8;

    public TimeSpan SynchronizationBudget { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
