using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Orchestration;

namespace LocalAssistant.Tests.Orchestration;

public sealed class ToolConfirmationStoreTests
{
    [Fact]
    public async Task RemoveAsyncInvalidatesThePendingConfirmation()
    {
        var store = new InMemoryToolConfirmationStore();
        var conversationId = Guid.NewGuid();
        var confirmation = CreateConfirmation(conversationId);
        await store.CreateAsync(confirmation, CancellationToken.None);

        Assert.True(await store.RemoveAsync(conversationId, CancellationToken.None));
        Assert.Null(await store.GetAsync(conversationId, CancellationToken.None));
        Assert.Null(await store.TakeAsync(
            conversationId,
            confirmation.ConfirmationId,
            CancellationToken.None));
        Assert.False(await store.RemoveAsync(conversationId, CancellationToken.None));
    }

    private static PendingToolConfirmation CreateConfirmation(Guid conversationId)
    {
        using var arguments = JsonDocument.Parse("{}");

        return new PendingToolConfirmation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            conversationId,
            "fake",
            "owner-a",
            new ToolCall("call-1", "example", arguments.RootElement.Clone()),
            [],
            DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
