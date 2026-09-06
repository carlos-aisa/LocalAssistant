namespace LocalAssistant.TerminalClient;

internal readonly record struct TerminalSize(int Width, int Height);

internal sealed record TerminalInputEvent(ConsoleKeyInfo? Key, bool IsEndOfInput)
{
    public static TerminalInputEvent EndOfInput { get; } = new(null, true);
}

internal interface ITerminalDriver
{
    bool TryInitialize();

    TerminalSize GetSize();

    TerminalInputEvent? TryReadInput();

    void Render(IReadOnlyList<string> lines);

    void Restore();
}

internal sealed class SystemTerminalDriver : ITerminalDriver
{
    public bool TryInitialize()
    {
        try
        {
            _ = Console.WindowWidth;
            _ = Console.WindowHeight;
            _ = Console.KeyAvailable;
            if (OperatingSystem.IsWindows())
            {
                var cursorVisible = Console.CursorVisible;
                Console.CursorVisible = cursorVisible;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    public TerminalSize GetSize()
    {
        try
        {
            return new TerminalSize(Console.WindowWidth, Console.WindowHeight);
        }
        catch (IOException)
        {
            return new TerminalSize(80, 24);
        }
        catch (InvalidOperationException)
        {
            return new TerminalSize(80, 24);
        }
    }

    public TerminalInputEvent? TryReadInput()
    {
        try
        {
            return Console.KeyAvailable
                ? new TerminalInputEvent(Console.ReadKey(intercept: true), false)
                : null;
        }
        catch (IOException)
        {
            return TerminalInputEvent.EndOfInput;
        }
        catch (InvalidOperationException)
        {
            return TerminalInputEvent.EndOfInput;
        }
    }

    public void Render(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Console.Clear();
        foreach (var line in lines)
        {
            Console.WriteLine(TerminalTextSanitizer.Normalize(line));
        }
    }

    public void Restore()
    {
        try
        {
            Console.CursorVisible = true;
            Console.WriteLine();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
