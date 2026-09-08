namespace LocalAssistant.TerminalClient;

internal enum TerminalKeyAction
{
    Ignore,
    Insert,
    DeletePrevious,
    Scroll,
    Submit,
    SubmitEmpty,
    CancelApplication,
    CloseChannel,
}

internal readonly record struct TerminalKeyIntent(
    TerminalKeyAction Action,
    char Character = '\0',
    int ScrollDelta = 0)
{
    public static readonly TerminalKeyIntent Ignore = new(TerminalKeyAction.Ignore);
    public static readonly TerminalKeyIntent CancelApplication = new(TerminalKeyAction.CancelApplication);
    public static readonly TerminalKeyIntent CloseChannel = new(TerminalKeyAction.CloseChannel);
    public static readonly TerminalKeyIntent Submit = new(TerminalKeyAction.Submit);
    public static readonly TerminalKeyIntent SubmitEmpty = new(TerminalKeyAction.SubmitEmpty);
    public static readonly TerminalKeyIntent DeletePrevious = new(TerminalKeyAction.DeletePrevious);

    public static TerminalKeyIntent Insert(char character) => new(TerminalKeyAction.Insert, Character: character);

    public static TerminalKeyIntent Scroll(int delta) => new(TerminalKeyAction.Scroll, ScrollDelta: delta);
}

/// <summary>
/// Pure translation of a key press and the host buffer state to an intention. It never
/// touches the console, the host or any state; the host executes the intention.
/// </summary>
internal static class TerminalKeyInterpreter
{
    internal const int PageScrollLines = 5;

    public static TerminalKeyIntent Interpret(ConsoleKeyInfo key, bool inputActive, bool bufferHasText)
    {
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;

        if (control && key.Key == ConsoleKey.C)
        {
            return TerminalKeyIntent.CancelApplication;
        }

        if (control && key.Key is ConsoleKey.D or ConsoleKey.Z)
        {
            return bufferHasText ? TerminalKeyIntent.Ignore : TerminalKeyIntent.CloseChannel;
        }

        switch (key.Key)
        {
            case ConsoleKey.PageUp:
                return TerminalKeyIntent.Scroll(PageScrollLines);
            case ConsoleKey.PageDown:
                return TerminalKeyIntent.Scroll(-PageScrollLines);
            case ConsoleKey.Enter:
                return inputActive ? TerminalKeyIntent.Submit : TerminalKeyIntent.Ignore;
            case ConsoleKey.Escape:
                return inputActive ? TerminalKeyIntent.SubmitEmpty : TerminalKeyIntent.Ignore;
            case ConsoleKey.Backspace:
                return inputActive && bufferHasText
                    ? TerminalKeyIntent.DeletePrevious
                    : TerminalKeyIntent.Ignore;
        }

        if (inputActive && !char.IsControl(key.KeyChar))
        {
            return TerminalKeyIntent.Insert(key.KeyChar);
        }

        return TerminalKeyIntent.Ignore;
    }
}
