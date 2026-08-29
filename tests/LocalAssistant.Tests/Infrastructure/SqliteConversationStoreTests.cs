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

    private static SqliteConversationStore CreateStore(string databasePath, TimeProvider? clock = null, int retentionDays = 30) => new(
        Options.Create(new SqliteConversationStoreOptions { DatabasePath = databasePath, RetentionDays = retentionDays }),
        clock ?? TimeProvider.System);
}
