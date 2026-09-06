using System.Globalization;
using System.Text;

namespace LocalAssistant.TerminalClient;

internal static class TerminalTextSanitizer
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '\n')
            {
                normalized.Append(character);
                continue;
            }

            if (character is >= ' ' and not '\u007F' &&
                (character < '\u0080' || character > '\u009F'))
            {
                normalized.Append(character);
                continue;
            }

            normalized.Append(string.Format(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)character));
        }

        return normalized.ToString();
    }

    public static string NormalizeSingleLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Normalize(value).Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
