namespace LocalAssistant.TerminalClient;

/// <summary>
/// Splits already-sanitized speech text without changing its contents. It favors a
/// sentence boundary, then whitespace, and only splits an overlong single word.
/// </summary>
internal static class SpokenTextSegmenter
{
    public const int FirstTargetLength = 160;
    public const int LaterTargetLength = 220;
    public const int MaximumLength = 320;

    public static IEnumerable<string> Segment(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var offset = 0;
        var first = true;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= MaximumLength)
            {
                yield return text[offset..];
                yield break;
            }

            var target = Math.Min(first ? FirstTargetLength : LaterTargetLength, MaximumLength);
            var boundary = FindBoundary(text, offset, target, remaining);
            yield return text.Substring(offset, boundary);
            offset += boundary;
            first = false;
        }
    }

    private static int FindBoundary(string text, int offset, int target, int remaining)
    {
        var maximum = Math.Min(MaximumLength, remaining);
        for (var index = Math.Min(target, maximum) - 1; index >= 0; index--)
        {
            if (text[offset + index] is '.' or '?' or '!' or ';' or ':')
            {
                return index + 1;
            }
        }

        for (var index = Math.Min(target, maximum) - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(text[offset + index]))
            {
                return index + 1;
            }
        }

        for (var index = maximum - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(text[offset + index]))
            {
                return index + 1;
            }
        }

        return maximum;
    }
}
