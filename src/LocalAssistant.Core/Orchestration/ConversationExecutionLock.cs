using System.Collections.Concurrent;

namespace LocalAssistant.Core.Orchestration;

public interface IConversationExecutionLock
{
    ValueTask<IDisposable> AcquireAsync(Guid conversationId, CancellationToken cancellationToken);
}

public sealed class InMemoryConversationExecutionLock : IConversationExecutionLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async ValueTask<IDisposable> AcquireAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
