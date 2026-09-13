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

    [Fact]
    public void AResponseUnderTheMaximumButOverTheFirstTargetStillSplitsForLowLatency()
    {
        // A single 260-character response fits entirely under MaximumLength (320), but
        // must still be split near FirstTargetLength so the first segment can start
        // playing before the rest is synthesized.
        var sentence = "Una frase con puntuación clara y contenido de relleno. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 5))[..260];

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.True(segments.Length > 1);
        Assert.InRange(segments[0].Length, 1, SpokenTextSegmenter.FirstTargetLength);
        Assert.Equal(text, string.Concat(segments));
    }

    [Fact]
    public void PrefersSentencePunctuationOverAWhitespaceBoundary()
    {
        var text = "Corta aquí. Pero no aquí " + new string('a', 200);

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.Equal("Corta aquí.", segments[0]);
        Assert.Equal(text, string.Concat(segments));
    }

    [Fact]
    public void RebalancesATrailingFragmentThatWouldBeTooSmallOnItsOwn()
    {
        // The natural (punctuation) cut lands at index 151, leaving only 16 trailing
        // characters. Since the whole 167-character text still fits under
        // MaximumLength, it must be kept whole instead of emitting a tiny 16-character
        // second segment.
        var text = new string('a', 150) + ". " + new string('b', 15);

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.Single(segments);
        Assert.Equal(text, segments[0]);
    }

    [Fact]
    public void DoesNotRebalanceWhenTheTrailingFragmentIsNotTooSmall()
    {
        // Same shape as the rebalancing case above, but the tail (60 characters) is
        // large enough to justify its own segment.
        var text = new string('a', 150) + ". " + new string('b', 60);

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.Equal(2, segments.Length);
        Assert.Equal(text, string.Concat(segments));
    }

    [Fact]
    public void PreservesAUrlNewlinesAndListMarkersLiterallyAcrossSegments()
    {
        var text = "Consulta https://example.com/docs/path?query=1&other=2 para más información.\n" +
            "- Primer punto de la lista con algo de contenido adicional.\n" +
            "- Segundo punto de la lista con más contenido para superar el límite inicial y forzar un corte real en algún punto del texto.";

        var segments = SpokenTextSegmenter.Segment(text).ToArray();

        Assert.Equal(text, string.Concat(segments));
        Assert.Contains(segments, segment => segment.Contains("https://example.com/docs/path?query=1&other=2"));
        Assert.All(segments, segment => Assert.InRange(segment.Length, 1, SpokenTextSegmenter.MaximumLength));
    }
}
