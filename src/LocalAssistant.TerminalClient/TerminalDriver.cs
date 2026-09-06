using System.Runtime.Versioning;

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

internal interface ISystemTerminal
{
    int WindowWidth { get; }

    int WindowHeight { get; }

    bool KeyAvailable { get; }

    bool CursorVisible { get; set; }

    ConsoleKeyInfo ReadKey(bool intercept);

    void Clear();

    void WriteLine(string value);
}

internal sealed class SystemConsoleTerminal : ISystemTerminal
{
    public int WindowWidth => Console.WindowWidth;

    public int WindowHeight => Console.WindowHeight;

    public bool KeyAvailable => Console.KeyAvailable;

    [SupportedOSPlatform("windows")]
    public bool CursorVisible
    {
        get => Console.CursorVisible;
        set => Console.CursorVisible = value;
    }

    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);

    public void Clear() => Console.Clear();

    public void WriteLine(string value) => Console.WriteLine(value);
}

internal sealed class SystemTerminalDriver : ITerminalDriver
{
    private readonly ISystemTerminal _terminal;
    private readonly bool _usesCursorVisibility;
    private bool _cursorStateCaptured;
    private bool _cursorWasVisible;
    private bool _restored;
    private TerminalSize? _lastKnownSize;

    public SystemTerminalDriver()
        : this(new SystemConsoleTerminal(), OperatingSystem.IsWindows())
    {
    }

    internal SystemTerminalDriver(ISystemTerminal terminal)
        : this(terminal, OperatingSystem.IsWindows())
    {
    }

    internal SystemTerminalDriver(ISystemTerminal terminal, bool usesCursorVisibility)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _usesCursorVisibility = usesCursorVisibility;
    }

    public bool TryInitialize()
    {
        _lastKnownSize = ReadAndRememberSize();
        _ = _terminal.KeyAvailable;

        if (_usesCursorVisibility)
        {
            _cursorWasVisible = _terminal.CursorVisible;
            _cursorStateCaptured = true;
            _terminal.CursorVisible = false;
        }

        _terminal.Clear();
        _terminal.WriteLine(string.Empty);
        return true;
    }

    public TerminalSize GetSize()
    {
        try
        {
            return ReadAndRememberSize();
        }
        catch (IOException) when (_lastKnownSize.HasValue)
        {
            return _lastKnownSize.Value;
        }
        catch (InvalidOperationException) when (_lastKnownSize.HasValue)
        {
            return _lastKnownSize.Value;
        }
        catch (PlatformNotSupportedException) when (_lastKnownSize.HasValue)
        {
            return _lastKnownSize.Value;
        }
    }

    public TerminalInputEvent? TryReadInput()
    {
        try
        {
            return _terminal.KeyAvailable
                ? new TerminalInputEvent(_terminal.ReadKey(intercept: true), false)
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
        _terminal.Clear();
        foreach (var line in lines)
        {
            _terminal.WriteLine(TerminalTextSanitizer.Normalize(line));
        }
    }

    public void Restore()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        try
        {
            if (_cursorStateCaptured)
            {
                _terminal.CursorVisible = _cursorWasVisible;
            }

            _terminal.WriteLine(string.Empty);
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private TerminalSize ReadAndRememberSize()
    {
        var size = new TerminalSize(_terminal.WindowWidth, _terminal.WindowHeight);
        _lastKnownSize = size;
        return size;
    }
}
