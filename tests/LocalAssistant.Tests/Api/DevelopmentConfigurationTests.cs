using Microsoft.Extensions.Configuration;

namespace LocalAssistant.Tests.Api;

public sealed class DevelopmentConfigurationTests
{
    [Fact]
    public void UsesTheCalibratedEmbeddinggemmaThresholdOnlyInDevelopment()
    {
        var baseConfiguration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var developmentConfiguration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();

        Assert.Equal(0.78, baseConfiguration.GetValue<double>("LocalAssistant:DocumentSemanticSearch:MinimumSimilarity"), 3);
        Assert.Equal(0.40, developmentConfiguration.GetValue<double>("LocalAssistant:DocumentSemanticSearch:MinimumSimilarity"), 3);
    }
}
