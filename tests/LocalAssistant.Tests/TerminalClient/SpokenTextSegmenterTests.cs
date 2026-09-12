using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class SpokenTextSegmenterTests
{
    [Fact]
    public void SegmentsLongTextWithoutLossOrDuplication()
    {
        var text = string.Concat(Enumerable.Repeat("Una frase con puntuación. ", 30));

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.Equal(text, string.Concat(segments));
        Assert.All(segments, segment => Assert.InRange(segment.Length, 1, SpokenTextSegmenter.MaximumLength));
        Assert.True(segments.Length > 1);
    }

    [Fact]
    public void SplitsAnOverlongWordOnlyAtTheMaximum()
    {
        var text = new string('x', SpokenTextSegmenter.MaximumLength + 5);

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.Equal([SpokenTextSegmenter.MaximumLength, 5], segments.Select(segment => segment.Length));
        Assert.Equal(text, string.Concat(segments));
    }
}
