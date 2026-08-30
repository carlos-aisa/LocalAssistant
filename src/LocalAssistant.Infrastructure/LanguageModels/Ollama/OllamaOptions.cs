namespace LocalAssistant.Infrastructure.LanguageModels.Ollama;

public sealed class OllamaOptions
{
    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    public string Model { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public bool Think { get; init; }

    public int ContextWindow { get; init; } = 4096;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);

    public bool IsEmbeddingConfigured => !string.IsNullOrWhiteSpace(EmbeddingModel);
}
