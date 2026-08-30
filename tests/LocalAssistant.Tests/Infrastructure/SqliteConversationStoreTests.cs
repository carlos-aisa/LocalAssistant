using LocalAssistant.Core.Conversations;
using LocalAssistant.Infrastructure.Conversations;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task PersistsOwnedConversationAcrossStoreInstances()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var databasePath = Path.Combine(directory.Path, "conversations.db");
        var conversationId = Guid.NewGuid();
        var firstStore = CreateStore(databasePath);

        await firstStore.GetOrCreateMetadataAsync(conversationId, "owner-a", CancellationToken.None);
        await firstStore.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, "Persist this message."),
            CancellationToken.None);

        var secondStore = CreateStore(databasePath);
        var metadata = await secondStore.GetMetadataAsync(conversationId, CancellationToken.None);
        var messages = await secondStore.GetMessagesAsync(conversationId, CancellationToken.None);

        Assert.Equal("owner-a", metadata?.OwnerPrincipalId);
        Assert.Equal("Persist this message.", Assert.Single(messages).Content);
    }

    [Fact]
    public async Task KeepsAnonymousConversationOutOfPersistentStore()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var persistentStore = CreateStore(Path.Combine(directory.Path, "conversations.db"));
        var store = new AuthenticatedConversationStore(persistentStore, new InMemoryConversationStore());
        var conversationId = Guid.NewGuid();

        await store.GetOrCreateMetadataAsync(conversationId, null, CancellationToken.None);
        await store.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, "Ephemeral message."),
            CancellationToken.None);

        Assert.Null(await persistentStore.GetMetadataAsync(conversationId, CancellationToken.None));
        Assert.Equal("Ephemeral message.", Assert.Single(await store.GetMessagesAsync(conversationId, CancellationToken.None)).Content);
    }

    [Fact]
    public async Task DeletesOnlyTheConversationOwnedByTheRequestedPrincipal()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var store = CreateStore(Path.Combine(directory.Path, "conversations.db"));
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(conversationId, "owner-a", CancellationToken.None);
        await store.AppendAsync(conversationId, new(ConversationRole.User, "Private message."), CancellationToken.None);

        Assert.False(await store.DeleteOwnedAsync(conversationId, "owner-b", CancellationToken.None));
        Assert.NotNull(await store.GetMetadataAsync(conversationId, CancellationToken.None));
        Assert.True(await store.DeleteOwnedAsync(conversationId, "owner-a", CancellationToken.None));
        Assert.Null(await store.GetMetadataAsync(conversationId, CancellationToken.None));
        Assert.Empty(await store.GetMessagesAsync(conversationId, CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticatedStoreDoesNotDeleteAnAnonymousConversation()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var persistentStore = CreateStore(Path.Combine(directory.Path, "conversations.db"));
        var ephemeralStore = new InMemoryConversationStore();
        var store = new AuthenticatedConversationStore(persistentStore, ephemeralStore);
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(conversationId, null, CancellationToken.None);
        await store.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, "Ephemeral message."),
            CancellationToken.None);

        Assert.False(await store.DeleteOwnedAsync(conversationId, "owner-a", CancellationToken.None));
        Assert.NotNull(await ephemeralStore.GetMetadataAsync(conversationId, CancellationToken.None));
        Assert.Equal(
            "Ephemeral message.",
            Assert.Single(await ephemeralStore.GetMessagesAsync(conversationId, CancellationToken.None)).Content);
    }

    [Fact]
    public async Task DeletesExpiredConversationsUsingTheInjectedClock()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));
        var store = CreateStore(Path.Combine(directory.Path, "conversations.db"), clock, retentionDays: 1);
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(conversationId, "owner-a", CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(1, await store.DeleteExpiredAsync(CancellationToken.None));
        Assert.Null(await store.GetMetadataAsync(conversationId, CancellationToken.None));
    }

    [Fact]
    public async Task RetrievesOnlyAnotherConversationOwnedByTheCurrentPrincipal()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var store = CreateStore(
            Path.Combine(directory.Path, "conversations.db"),
            retrievalEnabled: true);
        var matchingConversation = Guid.NewGuid();
        var otherOwnerConversation = Guid.NewGuid();
        var currentConversation = Guid.NewGuid();

        await store.GetOrCreateMetadataAsync(
            matchingConversation,
            "owner-a",
            CancellationToken.None);
        await store.AppendAsync(
            matchingConversation,
            new ConversationMessage(
                ConversationRole.User,
                "Planificamos los menus de la semana que viene."),
            CancellationToken.None);
        await store.GetOrCreateMetadataAsync(
            otherOwnerConversation,
            "owner-b",
            CancellationToken.None);
        await store.AppendAsync(
            otherOwnerConversation,
            new ConversationMessage(
                ConversationRole.User,
                "Menus privados de otro propietario."),
            CancellationToken.None);

        var result = await store.RetrieveAsync(
            "owner-a",
            currentConversation,
            "Tengo mas ideas para los menus.",
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal(matchingConversation, match.ConversationId);
        Assert.Contains("menus", match.Fragment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexesAnInactiveConversationOnlyAfterTheConfiguredDelay()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));
        var store = CreateStore(
            Path.Combine(directory.Path, "conversations.db"),
            clock,
            retrievalEnabled: true);
        var provider = new CountingEmbeddingProvider();
        var coordinator = new ConversationIndexingCoordinator(
            store,
            provider,
            new StaticSummaryProvider());
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(conversationId, "owner-a", CancellationToken.None);
        await store.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, "Plan weekly meals."),
            CancellationToken.None);

        Assert.Equal(0, await coordinator.ProcessPendingAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(1, await coordinator.ProcessPendingAsync(CancellationToken.None));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, await coordinator.ProcessPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindsAnInactiveConversationThroughItsLocalEmbedding()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));
        var store = CreateStore(
            Path.Combine(directory.Path, "conversations.db"),
            clock,
            retrievalEnabled: true);
        var embeddingProvider = new CountingEmbeddingProvider();
        var coordinator = new ConversationIndexingCoordinator(
            store,
            embeddingProvider,
            new StaticSummaryProvider());
        var indexedConversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(
            indexedConversationId,
            "owner-a",
            CancellationToken.None);
        await store.AppendAsync(
            indexedConversationId,
            new ConversationMessage(
                ConversationRole.User,
                "Planificamos comidas para los próximos días."),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));
        await coordinator.ProcessPendingAsync(CancellationToken.None);
        var retriever = new HybridConversationContextRetriever(
            store,
            embeddingProvider,
            new ConversationRetrievalOptions { Enabled = true });

        var result = await retriever.RetrieveAsync(
            "owner-a",
            Guid.NewGuid(),
            "Tengo nuevas ideas culinarias.",
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal(indexedConversationId, match.ConversationId);
        Assert.Equal("Meals", match.Topic);
        Assert.Equal("Meal planning.", match.Summary);
    }

    [Fact]
    public async Task KeepsTheEmbeddingWhenTheSummaryCannotBeGenerated()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));
        var store = CreateStore(
            Path.Combine(directory.Path, "conversations.db"),
            clock,
            retrievalEnabled: true);
        var embeddingProvider = new CountingEmbeddingProvider();
        var coordinator = new ConversationIndexingCoordinator(
            store,
            embeddingProvider,
            new FailingSummaryProvider());
        var indexedConversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(
            indexedConversationId,
            "owner-a",
            CancellationToken.None);
        await store.AppendAsync(
            indexedConversationId,
            new ConversationMessage(ConversationRole.User, "Planificamos comidas para los próximos días."),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(1, await coordinator.ProcessPendingAsync(CancellationToken.None));

        var matches = await store.RetrieveByEmbeddingAsync(
            "owner-a",
            Guid.NewGuid(),
            new TextEmbedding("test-model", [0.25f, -0.5f]),
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(indexedConversationId, match.ConversationId);
        Assert.Contains("Planificamos comidas", match.Summary);

        var recoveredCoordinator = new ConversationIndexingCoordinator(
            store,
            embeddingProvider,
            new StaticSummaryProvider());

        Assert.Equal(1, await recoveredCoordinator.ProcessPendingAsync(CancellationToken.None));
        Assert.Equal(1, embeddingProvider.CallCount);
    }

    [Fact]
    public async Task DoesNotStoreAnIndexWhenTheConversationChangesDuringProcessing()
    {
        using var directory = new LocalAssistant.Tests.Api.TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));
        var store = CreateStore(
            Path.Combine(directory.Path, "conversations.db"),
            clock,
            retrievalEnabled: true);
        var conversationId = Guid.NewGuid();
        await store.GetOrCreateMetadataAsync(conversationId, "owner-a", CancellationToken.None);
        await store.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, "Plan weekly meals."),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(15));
        var embeddingProvider = new CallbackEmbeddingProvider(async () =>
        {
            await store.AppendAsync(
                conversationId,
                new ConversationMessage(ConversationRole.Assistant, "Here are some ideas."),
                CancellationToken.None);
        });
        var coordinator = new ConversationIndexingCoordinator(
            store,
            embeddingProvider,
            new StaticSummaryProvider());

        Assert.Equal(0, await coordinator.ProcessPendingAsync(CancellationToken.None));
        Assert.Empty(await store.RetrieveByEmbeddingAsync(
            "owner-a",
            Guid.NewGuid(),
            new TextEmbedding("test-model", [0.25f, -0.5f]),
            CancellationToken.None));
    }

    private static SqliteConversationStore CreateStore(
        string databasePath,
        TimeProvider? clock = null,
        int retentionDays = 30,
        bool retrievalEnabled = false) => new(
        Options.Create(new SqliteConversationStoreOptions
        {
            DatabasePath = databasePath,
            RetentionDays = retentionDays,
        }),
        Options.Create(new ConversationRetrievalOptions
        {
            Enabled = retrievalEnabled,
        }),
        clock ?? TimeProvider.System);

    private sealed class CountingEmbeddingProvider : ITextEmbeddingProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<TextEmbedding> EmbedAsync(
            string text,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new TextEmbedding("test-model", [0.25f, -0.5f]));
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

    private sealed class FailingSummaryProvider : IConversationIndexSummaryProvider
    {
        public ValueTask<ConversationIndexSummary> SummarizeAsync(
            string text,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ConversationIndexSummary>(
                new InvalidOperationException("The local chat model is unavailable."));
    }

    private sealed class CallbackEmbeddingProvider(Func<Task> callback) : ITextEmbeddingProvider
    {
        public async ValueTask<TextEmbedding> EmbedAsync(
            string text,
            CancellationToken cancellationToken)
        {
            await callback();
            return new TextEmbedding("test-model", [0.25f, -0.5f]);
        }
    }
}
