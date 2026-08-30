namespace LocalAssistant.Core.Conversations;

public sealed record ConversationRetrievedContext(
    Guid ConversationId,
    DateTimeOffset LastActivityUtc,
    string Topic,
    string Summary,
    string Fragment,
    double Score);

public sealed record ConversationRetrievalResult(
    IReadOnlyList<ConversationRetrievedContext> Matches)
{
    public static ConversationRetrievalResult Empty { get; } = new([]);
}

public interface IConversationContextRetriever
{
    ValueTask<ConversationRetrievalResult> RetrieveAsync(
        string ownerPrincipalId,
        Guid currentConversationId,
        string message,
        CancellationToken cancellationToken);
}

public sealed class NullConversationContextRetriever : IConversationContextRetriever
{
    public ValueTask<ConversationRetrievalResult> RetrieveAsync(
        string ownerPrincipalId,
        Guid currentConversationId,
        string message,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConversationRetrievalResult.Empty);
}
