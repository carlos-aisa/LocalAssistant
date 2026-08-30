using System.Text.Json;
using LocalAssistant.Core.Documents;
using LocalAssistant.DocumentSearchEvaluation;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

var options = EvaluationOptions.Parse(args);
var corpusPath = Path.Combine(
    AppContext.BaseDirectory,
    "Fixtures",
    "document-semantic-search-corpus.json");
var corpus = DocumentSearchEvaluationCorpus.FromJson(
    await File.ReadAllTextAsync(corpusPath, CancellationToken.None));
using var httpClient = new HttpClient();
var embeddings = new OllamaTextEmbeddingProvider(
    httpClient,
    Options.Create(new OllamaOptions
    {
        Endpoint = options.Endpoint,
        EmbeddingModel = options.EmbeddingModel,
    }));
var evaluator = new DocumentSemanticSearchEvaluator(TimeProvider.System);
var literal = evaluator.EvaluateLiteral(corpus, options.Limit);
var semantic = await evaluator.EvaluateSemanticAsync(
    corpus,
    options.Limit,
    embeddings,
    CancellationToken.None);
var report = new
{
    evaluatedAtUtc = DateTimeOffset.UtcNow,
    literal,
    semantic,
};
var outputDirectory = Path.GetDirectoryName(options.OutputPath)!;
Directory.CreateDirectory(outputDirectory);
await File.WriteAllTextAsync(
    options.OutputPath,
    JsonSerializer.Serialize(report, EvaluationJson.SerializerOptions),
    CancellationToken.None);

Console.WriteLine($"Report: {options.OutputPath}");

internal sealed record EvaluationOptions(Uri Endpoint, string EmbeddingModel, int Limit, string OutputPath)
{
    public static EvaluationOptions Parse(string[] arguments)
    {
        var endpoint = new Uri("http://localhost:11434");
        var model = string.Empty;
        var limit = 3;
        var outputPath = Path.GetFullPath(Path.Combine("artifacts", "local-document-semantic-search.json"));

        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length)
            {
                throw new ArgumentException("Each option requires a value.");
            }

            switch (arguments[index])
            {
                case "--endpoint": endpoint = new Uri(arguments[index + 1], UriKind.Absolute); break;
                case "--model": model = arguments[index + 1]; break;
                case "--limit": limit = int.Parse(arguments[index + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--output": outputPath = Path.GetFullPath(arguments[index + 1]); break;
                default: throw new ArgumentException($"Unknown option '{arguments[index]}'.");
            }
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttp) ||
            !StringComparer.OrdinalIgnoreCase.Equals(endpoint.Host, "localhost"))
        {
            throw new ArgumentException("The endpoint must use local HTTP.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return new EvaluationOptions(endpoint, model.Trim(), limit, outputPath);
    }
}
