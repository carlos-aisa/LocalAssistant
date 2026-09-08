namespace LocalAssistant.TerminalClient;

/// <summary>
/// Bounded, width-independent view model for the session transcript. Retention happens
/// only in <see cref="Add"/>; <see cref="CreateView"/> is a pure query that materializes
/// just the requested viewport window.
/// </summary>
internal sealed class TerminalClientTuiTranscript
{
    internal const int MaximumCharacters = 65_536;
    internal const int MaximumReferenceLines = 2_000;
    internal const int ReferenceWidth = 40;

    private const string TruncationMarker = "[Earlier transcript content truncated]";

    // The '\n' that separates the marker from the retained tail counts against the
    // character budget, so the reserve is 39, not 38.
    private const int TruncationPrefixCharacters = 39;
    private const int TruncationPrefixReferenceLines = 1;

    private readonly LinkedList<Entry> _entries = [];
    private int _characterCount;
    private int _referenceLineCount;

    /// <summary>
    /// Diagnostic-only: number of logical segments visited by the most recent
    /// <see cref="CreateView"/> call. Used by tests to assert the reverse walk stops
    /// early; never part of the public surface and never affects the result.
    /// </summary>
    internal int LastViewVisitedSegmentCount { get; private set; }

    internal int CharacterCount => _characterCount;

    internal int ReferenceLineCount => _referenceLineCount;

    public void Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length == 0)
        {
            return;
        }

        var bounded = BoundToBudgets(entry);
        _entries.AddLast(bounded);
        _characterCount += bounded.CharacterCount;
        _referenceLineCount += bounded.ReferenceLineCount;
        EvictOldest();
    }

    public TranscriptView CreateView(int width, int viewportHeight, int scrollOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollOffset);

        LastViewVisitedSegmentCount = 0;

        if (viewportHeight == 0)
        {
            return new TranscriptView([], 0);
        }

        // The transcript can never yield more wrapped lines than it holds characters
        // (plus one per empty segment), so this cap bounds the work without truncating
        // any reachable window and without overflowing int on a hostile scrollOffset.
        var ceiling = (long)viewportHeight + MaximumCharacters + MaximumReferenceLines;
        var needed = (int)Math.Min((long)viewportHeight + scrollOffset, ceiling);

        var recentToOld = new List<string>();
        for (var node = _entries.Last; node is not null; node = node.Previous)
        {
            if (EmitEntryReversed(node.Value, width, needed, recentToOld))
            {
                break;
            }
        }

        var available = recentToOld.Count;
        var maxOffset = Math.Max(0, available - viewportHeight);
        var clamped = Math.Min(scrollOffset, maxOffset);

        var windowEnd = Math.Min(available, clamped + viewportHeight);
        var lines = new List<string>(windowEnd - clamped);
        for (var i = windowEnd - 1; i >= clamped; i--)
        {
            lines.Add(recentToOld[i]);
        }

        return new TranscriptView(lines, clamped);
    }

    private bool EmitEntryReversed(Entry entry, int width, int needed, List<string> sink)
    {
        var starts = entry.SegmentStarts;
        for (var i = starts.Length - 1; i >= 0; i--)
        {
            LastViewVisitedSegmentCount++;

            var segmentStart = starts[i];
            var segmentEnd = i + 1 < starts.Length ? starts[i + 1] - 1 : entry.Content.Length;
            var segmentLength = segmentEnd - segmentStart;

            if (segmentLength == 0)
            {
                sink.Add(string.Empty);
                if (sink.Count == needed)
                {
                    return true;
                }

                continue;
            }

            // Wrap boundaries are always measured from the start of the segment
            // (0, width, 2*width, ...); the chunks are emitted last-first so the buffer
            // stays ordered recent -> old.
            var lastChunkStart = (segmentLength - 1) / width * width;
            for (var chunkStart = lastChunkStart; chunkStart >= 0; chunkStart -= width)
            {
                var chunkLength = Math.Min(width, segmentLength - chunkStart);
                sink.Add(entry.Content.Substring(segmentStart + chunkStart, chunkLength));
                if (sink.Count == needed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EvictOldest()
    {
        while ((_characterCount > MaximumCharacters || _referenceLineCount > MaximumReferenceLines)
               && _entries.Count > 1)
        {
            var oldest = _entries.First!.Value;
            _entries.RemoveFirst();
            _characterCount -= oldest.CharacterCount;
            _referenceLineCount -= oldest.ReferenceLineCount;
        }
    }

    private static Entry BoundToBudgets(string content)
    {
        var lineBudget = MaximumReferenceLines - TruncationPrefixReferenceLines;
        var charBudget = MaximumCharacters - TruncationPrefixCharacters;

        if (content.Length <= MaximumCharacters)
        {
            var entry = Entry.Create(content);
            if (entry.ReferenceLineCount <= MaximumReferenceLines)
            {
                return entry;
            }

            var tail = KeepTail(entry, lineBudget, charBudget);
            return Entry.Create(TruncationMarker + "\n" + tail);
        }

        // Coarse character trim first, using Length only, so segment metadata is never
        // built over an unbounded entry.
        var charKept = content[^charBudget..];
        var trimmed = Entry.Create(charKept);
        if (trimmed.ReferenceLineCount > lineBudget)
        {
            charKept = KeepTail(trimmed, lineBudget, charBudget);
        }

        return Entry.Create(TruncationMarker + "\n" + charKept);
    }

    /// <summary>
    /// Longest suffix of <paramref name="entry"/> (starting at a segment boundary) that
    /// fits both the reference-line and the character budget once the marker is added.
    /// </summary>
    private static string KeepTail(Entry entry, int lineBudget, int charBudget)
    {
        var starts = entry.SegmentStarts;
        var accumulated = 0;
        var keepFrom = entry.Content.Length;

        for (var i = starts.Length - 1; i >= 0; i--)
        {
            var segmentStart = starts[i];
            var segmentEnd = i + 1 < starts.Length ? starts[i + 1] - 1 : entry.Content.Length;
            var cost = ReferenceLineCost(segmentEnd - segmentStart);
            if (accumulated + cost > lineBudget || entry.Content.Length - segmentStart > charBudget)
            {
                break;
            }

            accumulated += cost;
            keepFrom = segmentStart;
        }

        return entry.Content[keepFrom..];
    }

    private static int ReferenceLineCost(int segmentLength) =>
        Math.Max(1, (segmentLength + ReferenceWidth - 1) / ReferenceWidth);

    private sealed class Entry
    {
        private Entry(string content, int referenceLineCount, int[] segmentStarts)
        {
            Content = content;
            CharacterCount = content.Length;
            ReferenceLineCount = referenceLineCount;
            SegmentStarts = segmentStarts;
        }

        public string Content { get; }

        public int CharacterCount { get; }

        public int ReferenceLineCount { get; }

        public int[] SegmentStarts { get; }

        public static Entry Create(string content)
        {
            // Single pass over the string: record every logical segment start and
            // accumulate the reference-line cost without allocating a Split array.
            var starts = new List<int>();
            var referenceLines = 0;
            var segmentStart = 0;

            while (true)
            {
                var newline = content.IndexOf('\n', segmentStart);
                var segmentEnd = newline < 0 ? content.Length : newline;
                starts.Add(segmentStart);
                referenceLines += ReferenceLineCost(segmentEnd - segmentStart);

                if (newline < 0)
                {
                    break;
                }

                segmentStart = newline + 1;
            }

            return new Entry(content, referenceLines, [.. starts]);
        }
    }
}

internal readonly record struct TranscriptView(IReadOnlyList<string> Lines, int ClampedScrollOffset);
