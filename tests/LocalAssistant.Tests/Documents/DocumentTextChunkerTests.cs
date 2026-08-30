using LocalAssistant.Core.Documents;

namespace LocalAssistant.Tests.Documents;

public sealed class DocumentTextChunkerTests
{
    [Fact]
    public void SplitsTextIntoBoundedStableChunks()
    {
        var text = new string('a', DocumentTextChunker.MaximumChunkLength + 3);

        var chunks = DocumentTextChunker.Split(text);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(DocumentTextChunker.MaximumChunkLength, chunks[0].Length);
        Assert.Equal(3, chunks[1].Length);
    }

    [Fact]
    public void SplitsAtWhitespaceWhenTheTextAllowsIt()
    {
        var text = new string('a', DocumentTextChunker.MaximumChunkLength - 1)
            + " "
            + new string('b', 20);

        var chunks = DocumentTextChunker.Split(text);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('a', DocumentTextChunker.MaximumChunkLength - 1) + " ", chunks[0]);
        Assert.Equal(new string('b', 20), chunks[1]);
    }

    [Fact]
    public void LimitsAnExcerptWithoutAddingContent()
    {
        var text = new string('a', DocumentSemanticChunk.MaximumExcerptLength + 1);

        var excerpt = DocumentTextChunker.ToExcerpt(text);

        Assert.Equal(DocumentSemanticChunk.MaximumExcerptLength, excerpt.Length);
    }
}
