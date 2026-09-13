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

    /// <summary>
    /// Below this trailing length, a further split is not worth a whole extra
    /// synthesis call; the remainder is kept whole instead when it still fits under
    /// <see cref="MaximumLength"/>.
    /// </summary>
    private const int MinimumTailLength = 40;

    public static IEnumerable<string> Segment(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var offset = 0;
        var first = true;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            var target = first ? FirstTargetLength : LaterTargetLength;
            if (remaining <= target)
            {
                yield return text[offset..];
                yield break;
            }

            var boundary = FindBoundary(text, offset, target, remaining);
            var tail = remaining - boundary;
            if (tail > 0 && tail < MinimumTailLength && remaining <= MaximumLength)
            {
                // Splitting here would leave a too-small trailing fragment even though
                // the whole remainder still fits in one segment; keep it whole instead.
                yield return text[offset..];
                yield break;
            }

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
