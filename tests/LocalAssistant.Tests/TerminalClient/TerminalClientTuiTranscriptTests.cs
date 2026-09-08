using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientTuiTranscriptTests
{
    private const string Marker = "[Earlier transcript content truncated]";
    private const int MaxChars = TerminalClientTuiTranscript.MaximumCharacters;
    private const int MaxLines = TerminalClientTuiTranscript.MaximumReferenceLines;

    // TR-01
    [Fact]
    public void EmptySegmentsEachCountAsOneReferenceLine()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add("a\n\nb");

        Assert.Equal(4, transcript.CharacterCount);
        Assert.Equal(3, transcript.ReferenceLineCount);
    }

    // TR-02
    [Theory]
    [InlineData("a\n", 2)]
    [InlineData("a\nb\n", 3)]
    [InlineData("\n\n\n", 4)]
    public void ReferenceLineCostCountsTrailingAndConsecutiveNewlines(string entry, int expected)
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(entry);

        Assert.Equal(expected, transcript.ReferenceLineCount);
    }

    // TR-03
    [Fact]
    public void ReferenceLineCostWrapsAtReferenceWidth()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', 100));

        Assert.Equal(3, transcript.ReferenceLineCount);
    }

    // TR-04
    [Fact]
    public void TotalExactlyAtTheCharacterBudgetKeepsEveryEntry()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', MaxChars - 6));
        transcript.Add("second");

        Assert.Equal(MaxChars, transcript.CharacterCount);
        var view = transcript.CreateView(200, 5000, 0);
        Assert.Contains(view.Lines, line => line.StartsWith('a'));
        Assert.Contains(view.Lines, line => line.Contains("second", StringComparison.Ordinal));
    }

    // TR-05
    [Fact]
    public void OneCharacterOverTheBudgetEvictsTheOldestEntry()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add("oldest-" + new string('a', MaxChars - 10));
        transcript.Add("fresh");

        Assert.Equal(5, transcript.CharacterCount);
        var view = transcript.CreateView(200, 5000, 0);
        Assert.DoesNotContain(view.Lines, line => line.Contains("oldest-", StringComparison.Ordinal));
        Assert.Contains(view.Lines, line => line.Contains("fresh", StringComparison.Ordinal));
    }

    // TR-06
    [Fact]
    public void ReferenceLineBudgetExactThenExceededEvictsTheOldestEntry()
    {
        var exact = new TerminalClientTuiTranscript();
        exact.Add(new string('\n', MaxLines - 1));
        Assert.Equal(MaxLines, exact.ReferenceLineCount);

        exact.Add("\n");
        Assert.Equal(2, exact.ReferenceLineCount);
        var view = exact.CreateView(40, 10, 0);
        Assert.All(view.Lines, line => Assert.Equal(string.Empty, line));
    }

    // TR-07
    [Fact]
    public void ManyEntriesEvictTheOldestFirstInOrder()
    {
        var transcript = new TerminalClientTuiTranscript();
        for (var i = 0; i < 90; i++)
        {
            transcript.Add($"entry-{i:D3}-" + new string('x', 900));
        }

        Assert.True(transcript.CharacterCount <= MaxChars);
        var view = transcript.CreateView(1000, 5000, 0);
        Assert.DoesNotContain(view.Lines, line => line.Contains("entry-000", StringComparison.Ordinal));
        Assert.Contains(view.Lines, line => line.Contains("entry-089", StringComparison.Ordinal));

        // The surviving entries are a contiguous suffix: the first survivor's predecessor is gone.
        var firstSurvivor = Enumerable.Range(0, 90)
            .First(i => view.Lines.Any(line => line.Contains($"entry-{i:D3}", StringComparison.Ordinal)));
        Assert.InRange(firstSurvivor, 1, 89);
        for (var i = firstSurvivor; i < 90; i++)
        {
            Assert.Contains(view.Lines, line => line.Contains($"entry-{i:D3}", StringComparison.Ordinal));
        }
    }

    // TR-08
    [Fact]
    public void EntryOverTheCharacterBudgetKeepsItsTailWithTheMarkerAndMeetsTheBudgetExactly()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', MaxChars + 500) + "TAIL-MARK");

        Assert.Equal(MaxChars, transcript.CharacterCount);
        var view = transcript.CreateView(80, 5000, 0);
        Assert.Contains(view.Lines, line => line.Contains(Marker, StringComparison.Ordinal));
        Assert.Contains(view.Lines, line => line.Contains("TAIL-MARK", StringComparison.Ordinal));
    }

    // TR-09
    [Fact]
    public void EntryOverTheReferenceLineBudgetKeepsItsTailWithTheMarkerAndIsNotRemoved()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('\n', MaxLines + 100) + "endtail");

        Assert.True(transcript.ReferenceLineCount <= MaxLines);
        Assert.True(transcript.CharacterCount <= MaxChars);
        var view = transcript.CreateView(40, MaxLines, 0);
        Assert.Contains(view.Lines, line => line.Contains(Marker, StringComparison.Ordinal));
        Assert.Contains(view.Lines, line => line.Contains("endtail", StringComparison.Ordinal));
    }

    // TR-10
    [Fact]
    public void EntryThatIsAlmostAllNewlinesTriggersBothTrimsAndAddsTheMarkerOnce()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('\n', 70_000));

        Assert.True(transcript.CharacterCount <= MaxChars);
        Assert.True(transcript.ReferenceLineCount <= MaxLines);
        var view = transcript.CreateView(40, MaxLines, 0);
        Assert.Equal(1, view.Lines.Count(line => line.Contains(Marker, StringComparison.Ordinal)));
    }

    // TR-11
    [Fact]
    public void EntryTrimmedByCharactersThenByLinesDoesNotDuplicateTheMarker()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', 66_000) + new string('\n', 2_900) + "\nEND");

        Assert.True(transcript.CharacterCount <= MaxChars);
        Assert.True(transcript.ReferenceLineCount <= MaxLines);
        var view = transcript.CreateView(40, MaxLines, 0);
        Assert.Equal(1, view.Lines.Count(line => line.Contains(Marker, StringComparison.Ordinal)));
        Assert.Contains(view.Lines, line => line.Contains("END", StringComparison.Ordinal));
    }

    // TR-12
    [Fact]
    public void EvictingATruncatedEntryLeavesNoStaleContent()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', MaxChars + 5_000) + "OLD");
        transcript.Add("fresh-small");

        var view = transcript.CreateView(80, 5000, 0);
        Assert.Contains(view.Lines, line => line.Contains("fresh-small", StringComparison.Ordinal));
        Assert.DoesNotContain(view.Lines, line => line.Contains(new string('a', 60), StringComparison.Ordinal));
        Assert.DoesNotContain(view.Lines, line => line.Contains(Marker, StringComparison.Ordinal));
    }

    // TR-13
    [Fact]
    public void SegmentIndicesReflectTheFinalContentAfterTruncation()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('a', MaxChars) + "\n" + new string('b', 100));

        var view = transcript.CreateView(40, 10, 0);

        Assert.Equal(new string('b', 40), view.Lines[^3]);
        Assert.Equal(new string('b', 40), view.Lines[^2]);
        Assert.Equal(new string('b', 20), view.Lines[^1]);
    }

    // TR-14
    [Fact]
    public void WrappingKeepsBoundariesMeasuredFromTheStartOfTheSegment()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add(new string('c', 100));

        var view = transcript.CreateView(40, 10, 0);

        Assert.Equal(
            new[] { new string('c', 40), new string('c', 40), new string('c', 20) },
            view.Lines);
    }

    // TR-15
    [Fact]
    public void ScrollOffsetZeroShowsTheMostRecentLines()
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add("old");
        transcript.Add("new");

        var view = transcript.CreateView(40, 1, 0);

        string[] expected = ["new"];
        Assert.Equal(expected, view.Lines);
        Assert.Equal(0, view.ClampedScrollOffset);
    }

    // TR-16
    [Fact]
    public void IntermediateScrollOffsetReturnsAChronologicalWindow()
    {
        var transcript = new TerminalClientTuiTranscript();
        foreach (var entry in new[] { "L0", "L1", "L2", "L3", "L4" })
        {
            transcript.Add(entry);
        }

        var view = transcript.CreateView(40, 2, 1);

        string[] expected = ["L2", "L3"];
        Assert.Equal(expected, view.Lines);
        Assert.Equal(1, view.ClampedScrollOffset);
    }

    // TR-17
    [Fact]
    public void ScrollOffsetAboveTheMaximumIsClampedToTheOldestWindow()
    {
        var transcript = new TerminalClientTuiTranscript();
        foreach (var entry in new[] { "A", "B", "C" })
        {
            transcript.Add(entry);
        }

        var view = transcript.CreateView(40, 2, 100);

        string[] expected = ["A", "B"];
        Assert.Equal(expected, view.Lines);
        Assert.Equal(1, view.ClampedScrollOffset);
    }

    // TR-18
    [Fact]
    public void HugeScrollOffsetDoesNotOverflowAndClampsToAValidOffset()
    {
        var transcript = new TerminalClientTuiTranscript();
        foreach (var entry in new[] { "A", "B", "C", "D", "E" })
        {
            transcript.Add(entry);
        }

        var view = transcript.CreateView(40, 3, int.MaxValue - 5);

        string[] expected = ["A", "B", "C"];
        Assert.Equal(2, view.ClampedScrollOffset);
        Assert.Equal(expected, view.Lines);
    }

    // TR-19
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ZeroViewportHeightReturnsAnEmptyView(int scrollOffset)
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add("something");

        var view = transcript.CreateView(40, 0, scrollOffset);

        Assert.Empty(view.Lines);
        Assert.Equal(0, view.ClampedScrollOffset);
    }

    // TR-20
    [Fact]
    public void LineCountNeverExceedsTheViewportHeight()
    {
        var transcript = new TerminalClientTuiTranscript();
        for (var i = 0; i < 40; i++)
        {
            transcript.Add($"line-{i:D2}");
        }

        foreach (var height in new[] { 1, 5, 8, 20, 100 })
        {
            var view = transcript.CreateView(40, height, 0);
            Assert.True(view.Lines.Count <= height);
        }
    }

    // TR-21
    [Fact]
    public void RepeatedCreateViewIsPureAndDoesNotAlterCounters()
    {
        var transcript = new TerminalClientTuiTranscript();
        for (var i = 0; i < 20; i++)
        {
            transcript.Add($"entry-{i:D2}-" + new string('y', 60));
        }

        var characters = transcript.CharacterCount;
        var referenceLines = transcript.ReferenceLineCount;

        var first = transcript.CreateView(40, 6, 2);
        var second = transcript.CreateView(40, 6, 2);

        Assert.Equal(first.Lines, second.Lines);
        Assert.Equal(first.ClampedScrollOffset, second.ClampedScrollOffset);
        Assert.Equal(characters, transcript.CharacterCount);
        Assert.Equal(referenceLines, transcript.ReferenceLineCount);
    }

    // TR-22
    [Fact]
    public void QueryingWideThenNarrowThenWideReturnsTheSameStoredContent()
    {
        var transcript = new TerminalClientTuiTranscript();
        for (var i = 0; i < 20; i++)
        {
            transcript.Add($"item-{i:D2}-" + new string('z', 90));
        }

        var wideBefore = transcript.CreateView(40, 5000, 0);
        transcript.CreateView(12, 5000, 0);
        var wideAfter = transcript.CreateView(40, 5000, 0);

        Assert.Equal(wideBefore.Lines, wideAfter.Lines);
    }

    // TR-23
    [Fact]
    public void QueryingNarrowNeverEvictsEntries()
    {
        var transcript = new TerminalClientTuiTranscript();
        for (var i = 0; i < 50; i++)
        {
            transcript.Add($"keep-{i:D2}");
        }

        var characters = transcript.CharacterCount;
        transcript.CreateView(4, 5, 0);
        transcript.CreateView(6, 3, 10);

        Assert.Equal(characters, transcript.CharacterCount);
        var wide = transcript.CreateView(200, 5000, 0);
        for (var i = 0; i < 50; i++)
        {
            Assert.Contains(wide.Lines, line => line.Contains($"keep-{i:D2}", StringComparison.Ordinal));
        }
    }

    // TR-24
    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(-1, 5, 0)]
    [InlineData(40, -1, 0)]
    [InlineData(40, 5, -1)]
    public void InvalidArgumentsThrow(int width, int viewportHeight, int scrollOffset)
    {
        var transcript = new TerminalClientTuiTranscript();
        transcript.Add("x");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => transcript.CreateView(width, viewportHeight, scrollOffset));
    }

    // TR-25
    [Fact]
    public void ASmallViewOverAFullTranscriptReturnsExactlyTheRequestedWindow()
    {
        var transcript = FullTranscript();
        var characters = transcript.CharacterCount;

        var view = transcript.CreateView(40, 8, 0);

        Assert.Equal(8, view.Lines.Count);
        Assert.Equal(characters, transcript.CharacterCount);
    }

    // TR-26
    [Fact]
    public void TheReverseWalkStopsEarlyInsteadOfVisitingEverySegment()
    {
        var transcript = FullTranscript();

        transcript.CreateView(40, 8, 0);

        // The window needs 8 lines; the newest entry alone supplies them, so only a
        // handful of segments are ever touched even though the transcript holds hundreds.
        Assert.True(
            transcript.LastViewVisitedSegmentCount <= 8 + 10,
            $"visited {transcript.LastViewVisitedSegmentCount} segments");
    }

    // TR-27
    [Fact]
    public void ReducingThenRestoringTheQueriedWidthDoesNotDestroyContent()
    {
        var transcript = new TerminalClientTuiTranscript();
        for (var i = 0; i < 40; i++)
        {
            transcript.Add($"keep-{i:D2}-" + new string('x', 780));
        }

        var characters = transcript.CharacterCount;

        transcript.CreateView(12, 8, 0);
        transcript.CreateView(200, 8, 0);
        var wide = transcript.CreateView(200, 5000, 0);

        Assert.Equal(characters, transcript.CharacterCount);
        Assert.Contains(wide.Lines, line => line.Contains("keep-00", StringComparison.Ordinal));
        Assert.Contains(wide.Lines, line => line.Contains("keep-39", StringComparison.Ordinal));
    }

    private static TerminalClientTuiTranscript FullTranscript()
    {
        var transcript = new TerminalClientTuiTranscript();
        // Entries with several 50-char segments so hundreds of segments accumulate.
        for (var i = 0; i < 200; i++)
        {
            var entry = string.Join('\n', Enumerable.Repeat($"segment-{i:D3}-" + new string('s', 38), 10));
            transcript.Add(entry);
        }

        return transcript;
    }
}
