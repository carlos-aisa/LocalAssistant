namespace LocalAssistant.Core.Conversations;

public sealed class ConversationIndexSummary
{
    public ConversationIndexSummary(
        string topic,
        string summary,
        IReadOnlyList<string> keywords)
    {
        Topic = ValidateText(topic, nameof(topic), maximumLength: 120);
        Summary = ValidateText(summary, nameof(summary), maximumLength: 1_000);
        ArgumentNullException.ThrowIfNull(keywords);

        Keywords = keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => ValidateText(keyword, nameof(keywords), maximumLength: 80))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    public string Topic { get; }

    public string Summary { get; }

    public IReadOnlyList<string> Keywords { get; }

    private static string ValidateText(string text, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        var normalizedText = text.Trim();
        if (normalizedText.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value cannot exceed {maximumLength} characters.");
        }

        return normalizedText;
    }
}

public interface IConversationIndexSummaryProvider
{
    ValueTask<ConversationIndexSummary> SummarizeAsync(
        string text,
        CancellationToken cancellationToken);
}
