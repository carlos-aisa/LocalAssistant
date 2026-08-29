using LocalAssistant.Core.Memory;
using LocalAssistant.Infrastructure.Conversations;
using LocalAssistant.Infrastructure.Memory;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class SqlitePersonalMemoryStoreTests
{
    [Fact]
    public async Task PersistsOwnedMemoryAcrossStoreInstances()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "private-data.db");
        var firstStore = CreateStore(databasePath);

        var created = await firstStore.CreateAsync(
            "owner-a",
            new PersonalMemoryDraft("Prefers lactose-free alternatives."),
            CancellationToken.None);

        var secondStore = CreateStore(databasePath);
        var memories = await secondStore.ListOwnedAsync(
            "owner-a",
            new PersonalMemoryListQuery(),
            CancellationToken.None);

        var memory = Assert.Single(memories);
        Assert.Equal(created.Id, memory.Id);
        Assert.Equal("Prefers lactose-free alternatives.", memory.Text);
        Assert.Equal("owner-a", memory.OwnerPrincipalId);
    }

    [Fact]
    public async Task ListsOnlyTheRequestedOwnersMemoriesInModifiedOrderAndWithinLimit()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));
        var store = CreateStore(Path.Combine(directory.Path, "private-data.db"), clock);
        await store.CreateAsync("owner-a", new PersonalMemoryDraft("First."), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.CreateAsync("owner-b", new PersonalMemoryDraft("Other owner."), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.CreateAsync("owner-a", new PersonalMemoryDraft("Second."), CancellationToken.None);

        var memories = await store.ListOwnedAsync(
            "owner-a",
            new PersonalMemoryListQuery(limit: 1),
            CancellationToken.None);

        Assert.Equal("Second.", Assert.Single(memories).Text);
    }

    [Fact]
    public async Task DeletesOnlyTheMemoryOwnedByTheRequestedPrincipal()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(Path.Combine(directory.Path, "private-data.db"));
        var memory = await store.CreateAsync(
            "owner-a",
            new PersonalMemoryDraft("Private preference."),
            CancellationToken.None);

        Assert.False(await store.DeleteOwnedAsync(memory.Id, "owner-b", CancellationToken.None));
        Assert.Single(await store.ListOwnedAsync(
            "owner-a",
            new PersonalMemoryListQuery(),
            CancellationToken.None));
        Assert.True(await store.DeleteOwnedAsync(memory.Id, "owner-a", CancellationToken.None));
        Assert.Empty(await store.ListOwnedAsync(
            "owner-a",
            new PersonalMemoryListQuery(),
            CancellationToken.None));
    }

    [Fact]
    public async Task PurgesExpiredMemoriesUsingTheInjectedClock()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero));
        var store = CreateStore(Path.Combine(directory.Path, "private-data.db"), clock, retentionDays: 1);
        var memory = await store.CreateAsync(
            "owner-a",
            new PersonalMemoryDraft("Temporary preference."),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(2));

        Assert.Empty(await store.ListOwnedAsync(
            "owner-a",
            new PersonalMemoryListQuery(),
            CancellationToken.None));
        Assert.False(await store.DeleteOwnedAsync(memory.Id, "owner-a", CancellationToken.None));
    }

    private static SqlitePersonalMemoryStore CreateStore(
        string databasePath,
        TimeProvider? clock = null,
        int retentionDays = 30) => new(
        Options.Create(new SqliteConversationStoreOptions
        {
            DatabasePath = databasePath,
            RetentionDays = retentionDays,
        }),
        clock ?? TimeProvider.System);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalAssistant.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
