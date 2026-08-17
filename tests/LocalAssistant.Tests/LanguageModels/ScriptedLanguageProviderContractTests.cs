using LocalAssistant.Core.LanguageModels;

namespace LocalAssistant.Tests.LanguageModels;

public sealed class ScriptedLanguageProviderContractTests : LanguageProviderContractTests
{
    protected override ProviderLease CreateProvider(LanguageProviderResponse response)
    {
        var provider = new ScriptedLanguageProvider(
            [ScriptedLanguageProvider.Return(response)],
            "contract-scripted");
        return new ProviderLease(provider);
    }
}
