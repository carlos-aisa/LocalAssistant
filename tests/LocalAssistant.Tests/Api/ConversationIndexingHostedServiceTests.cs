using System.Net;
using System.Text;
using LocalAssistant.Api.HostedServices;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Infrastructure.Conversations;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Api;

public sealed class ConversationIndexingHostedServiceTests
{
    [Fact]
    public async Task ProcessesPendingConversationsAfterValidatingTheEmbeddingModel()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));
        var retrievalOptions = new ConversationRetrievalOptions
        {
            Enabled = true,
            IndexingDelay = TimeSpan.FromMinutes(15),
            IndexingPollInterval = TimeSpan.FromDays(1),
        };
        var store = new SqliteConversationStore(
            Options.Create(new SqliteConversationStoreOptions
            {
                DatabasePath = Path.Combine(directory.Path, "conversations.db"),
            }),
            Options.Create(retrievalOptions),
            clock);
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(conversationId, "owner-a", CancellationToken.None);
        await store.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, "Plan weekly meals."),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));

        var embeddingProvider = new SignalingEmbeddingProvider();
        var coordinator = new ConversationIndexingCoordinator(
            store,
            embeddingProvider,
            new StaticSummaryProvider(),
            NullLogger<ConversationIndexingCoordinator>.Instance);
        using var handler = new StaticHttpMessageHandler(
            """{ "capabilities": ["embedding"] }""");
        using var client = new HttpClient(handler);
        var ollamaOptions = new OllamaOptions
        {
            Endpoint = new Uri("http://localhost:11434"),
            EmbeddingModel = "embedding-model",
        };
        var inspector = new OllamaModelInspector(
            client,
            Options.Create(ollamaOptions),
            new OllamaModelValidationCache());
        var service = new ConversationIndexingHostedService(
            coordinator,
            Options.Create(retrievalOptions),
            Options.Create(ollamaOptions),
            inspector,
            NullLogger<ConversationIndexingHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await embeddingProvider.EmbeddingRequested.Task.WaitAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    private sealed class SignalingEmbeddingProvider : ITextEmbeddingProvider
    {
        public TaskCompletionSource EmbeddingRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TextEmbedding> EmbedAsync(
            string text,
            CancellationToken cancellationToken)
        {
            EmbeddingRequested.SetResult();
            return ValueTask.FromResult(new TextEmbedding("embedding-model", [0.25f, -0.5f]));
        }
    }

    private sealed class StaticSummaryProvider : IConversationIndexSummaryProvider
    {
        public ValueTask<ConversationIndexSummary> SummarizeAsync(
            string text,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ConversationIndexSummary("Meals", "Meal planning.", ["meals"]));
    }

    private sealed class StaticHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
