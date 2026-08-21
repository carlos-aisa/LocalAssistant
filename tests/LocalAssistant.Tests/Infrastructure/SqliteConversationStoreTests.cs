using LocalAssistant.Core.Conversations;
using LocalAssistant.Infrastructure.Conversations;
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

    private static SqliteConversationStore CreateStore(string databasePath) => new(
        Options.Create(new SqliteConversationStoreOptions { DatabasePath = databasePath }));
}
