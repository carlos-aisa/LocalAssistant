namespace LocalAssistant.Core.Conversations;

public sealed record TextEmbedding
{
    public TextEmbedding(string model, IReadOnlyList<float> values)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("An embedding model is required.", nameof(model));
        }

        if (values.Count == 0 || values.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException(
                "An embedding requires finite values.",
                nameof(values));
        }

        Model = model;
        Values = values;
    }

    public string Model { get; }

    public IReadOnlyList<float> Values { get; }
}

public interface ITextEmbeddingProvider
{
    ValueTask<TextEmbedding> EmbedAsync(
        string text,
        CancellationToken cancellationToken);
}
