using LocalAssistant.Api.Fakes;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.LanguageModels;

public sealed record LanguageProviderSelection(
    ILanguageProvider? Provider,
    string? ErrorField = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Provider is not null;
}

public sealed class LanguageProviderSelector
{
    private readonly FakeLanguageProviderFactory _fakeProviderFactory;
    private readonly OllamaLanguageProvider _ollamaProvider;
    private readonly OllamaModelInspector _ollamaModelInspector;
    private readonly OllamaOptions _ollamaOptions;

    public LanguageProviderSelector(
        FakeLanguageProviderFactory fakeProviderFactory,
        OllamaLanguageProvider ollamaProvider,
        OllamaModelInspector ollamaModelInspector,
        IOptions<OllamaOptions> ollamaOptions)
    {
        _fakeProviderFactory = fakeProviderFactory;
        _ollamaProvider = ollamaProvider;
        _ollamaModelInspector = ollamaModelInspector;
        _ollamaOptions = ollamaOptions.Value;
    }

    public async Task<LanguageProviderSelection> SelectAsync(
        string? providerName,
        string? fakeScenario,
        CancellationToken cancellationToken)
    {
        if (string.Equals(providerName, "fake", StringComparison.OrdinalIgnoreCase))
        {
            return _fakeProviderFactory.TryCreate(fakeScenario ?? string.Empty, out var fakeProvider)
                ? new LanguageProviderSelection(fakeProvider)
                : new LanguageProviderSelection(
                    null,
                    "scenario",
                    "Supported fake scenarios are 'direct' and 'time'.");
        }

        if (string.Equals(providerName, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (!_ollamaOptions.IsConfigured)
            {
                return new LanguageProviderSelection(
                    null,
                    "provider",
                    "Ollama requires LocalAssistant:Ollama:Model configuration.");
            }

            var validation = await _ollamaModelInspector.ValidateAsync(cancellationToken);
            return validation.IsValid
                ? new LanguageProviderSelection(_ollamaProvider)
                : new LanguageProviderSelection(null, "provider", validation.ErrorMessage);
        }

        return new LanguageProviderSelection(
            null,
            "provider",
            "Supported providers are 'fake' and 'ollama'.");
    }
}
