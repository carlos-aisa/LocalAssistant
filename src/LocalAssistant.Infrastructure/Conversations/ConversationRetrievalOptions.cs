namespace LocalAssistant.Infrastructure.Conversations;

public sealed class ConversationRetrievalOptions
{
    public const string SectionName = "LocalAssistant:ConversationRetrieval";

    public bool Enabled { get; set; }

    public int MaximumMatches { get; set; } = 3;

    public int MaximumContextCharacters { get; set; } = 500;

    public TimeSpan IndexingDelay { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan IndexingPollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
