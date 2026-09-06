namespace LocalAssistant.TerminalClient;

internal sealed class TerminalClientTuiTranscript
{
    internal const int MaximumCharacters = 65_536;
    internal const int MaximumWrappedLines = 2_000;
    private const string TruncationMarker = "[Earlier transcript content truncated]";

    private readonly LinkedList<string> _entries = [];
    private int _characterCount;

    public void Add(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length == 0)
        {
            return;
        }

        var boundedEntry = KeepTailWithinCharacterBudget(entry);
        _entries.AddLast(boundedEntry);
        _characterCount += boundedEntry.Length;
        TrimCharacters();
    }

    public IReadOnlyList<string> CreateLines(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        TrimWrappedLines(width);

        var lines = new List<string>();
        foreach (var entry in _entries)
        {
            AddWrappedLines(lines, entry, width, MaximumWrappedLines);
        }

        return lines;
    }

    private static void AddWrappedLines(List<string> lines, string entry, int width, int limit)
    {
        foreach (var sourceLine in entry.Split('\n'))
        {
            if (lines.Count == limit)
            {
                return;
            }

            if (sourceLine.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            for (var offset = 0; offset < sourceLine.Length && lines.Count < limit; offset += width)
            {
                lines.Add(sourceLine.Substring(offset, Math.Min(width, sourceLine.Length - offset)));
            }
        }
    }

    private static string KeepTailWithinCharacterBudget(string entry)
    {
        if (entry.Length <= MaximumCharacters)
        {
            return entry;
        }

        var retainedLength = MaximumCharacters - TruncationMarker.Length;
        return TruncationMarker + entry[^retainedLength..];
    }

    private void TrimCharacters()
    {
        while (_characterCount > MaximumCharacters && _entries.First is not null)
        {
            var entry = _entries.First!.Value;
            _entries.RemoveFirst();
            _characterCount -= entry.Length;
        }
    }

    private void TrimWrappedLines(int width)
    {
        while (_entries.Count > 1 && CountWrappedLines(width) > MaximumWrappedLines)
        {
            var entry = _entries.First!.Value;
            _entries.RemoveFirst();
            _characterCount -= entry.Length;
        }

        if (_entries.Count == 0)
        {
            return;
        }

        var onlyEntry = _entries.First!;
        if (onlyEntry != _entries.Last || CountWrappedLines(width) <= MaximumWrappedLines)
        {
            return;
        }

        var maximumRetainedCharacters = Math.Max(
            0,
            (MaximumWrappedLines * width) - TruncationMarker.Length);
        var currentEntry = onlyEntry.Value ?? string.Empty;
        var trimmed = TruncationMarker + currentEntry[^maximumRetainedCharacters..];
        _characterCount -= currentEntry.Length;
        onlyEntry.Value = trimmed;
        _characterCount += trimmed.Length;
    }

    private int CountWrappedLines(int width)
    {
        var count = 0;
        foreach (var entry in _entries)
        {
            foreach (var sourceLine in entry.Split('\n'))
            {
                count += Math.Max(1, (sourceLine.Length + width - 1) / width);
                if (count > MaximumWrappedLines)
                {
                    return count;
                }
            }
        }

        return count;
    }
}
