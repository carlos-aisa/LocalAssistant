using LocalAssistant.Infrastructure.Conversations;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.HostedServices;

public sealed partial class ConversationIndexingHostedService : BackgroundService
{
    private readonly ConversationIndexingCoordinator _coordinator;
    private readonly ConversationRetrievalOptions _retrievalOptions;
    private readonly OllamaOptions _ollamaOptions;
    private readonly ILogger<ConversationIndexingHostedService> _logger;

    public ConversationIndexingHostedService(
        ConversationIndexingCoordinator coordinator,
        IOptions<ConversationRetrievalOptions> retrievalOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<ConversationIndexingHostedService> logger)
    {
        _coordinator = coordinator;
        _retrievalOptions = retrievalOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_retrievalOptions.Enabled || !_ollamaOptions.IsEmbeddingConfigured)
        {
            return;
        }

        using var timer = new PeriodicTimer(_retrievalOptions.IndexingPollInterval);
        do
        {
            try
            {
                await _coordinator.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                IndexingFailed(_logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Conversation semantic indexing failed and will be retried.")]
    private static partial void IndexingFailed(ILogger logger, Exception exception);
}
