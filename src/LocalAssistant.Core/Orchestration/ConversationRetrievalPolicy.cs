namespace LocalAssistant.Core.Orchestration;

public static class ConversationRetrievalPolicy
{
    private static readonly string[] ExplicitHistoryPhrases =
    [
        "recuerda",
        "qué dijimos",
        "que dijimos",
        "busca en nuestras conversaciones",
        "busca en mis conversaciones",
        "hablamos de",
    ];

    private static readonly string[] ContinuationPhrases =
    [
        "seguimos",
        "más ideas",
        "mas ideas",
        "lo de ",
        "retom",
    ];

    public static bool ShouldRetrieve(string message, bool isFirstUserTurn)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim();
        if (ExplicitHistoryPhrases.Any(phrase =>
                normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return normalized.Length >= 18 &&
               ContinuationPhrases.Any(phrase =>
                   normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}
