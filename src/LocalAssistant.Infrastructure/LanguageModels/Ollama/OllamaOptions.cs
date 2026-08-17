namespace LocalAssistant.Infrastructure.LanguageModels.Ollama;

public sealed class OllamaOptions
{
    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    public string Model { get; init; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
}
