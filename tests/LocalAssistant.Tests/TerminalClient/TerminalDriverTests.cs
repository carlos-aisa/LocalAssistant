using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalDriverTests
{
    [Fact]
    public void InitializationProbesTheRequiredOperationsWithoutConsumingInput()
    {
        var terminal = new TestSystemTerminal();
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: true);

        var initialized = driver.TryInitialize();

        Assert.True(initialized);
        Assert.Equal(1, terminal.WidthReadCount);
        Assert.Equal(1, terminal.HeightReadCount);
        Assert.Equal(1, terminal.KeyAvailableReadCount);
        Assert.Equal(1, terminal.CursorReadCount);
        Assert.Equal(1, terminal.CursorWriteCount);
        Assert.Equal(1, terminal.ClearCount);
        Assert.Equal([string.Empty], terminal.WrittenLines);
        Assert.Equal(0, terminal.ReadKeyCount);
    }

    [Theory]
    [InlineData(FailurePoint.Width)]
    [InlineData(FailurePoint.Height)]
    [InlineData(FailurePoint.KeyAvailable)]
    [InlineData(FailurePoint.Cursor)]
    [InlineData(FailurePoint.Clear)]
    [InlineData(FailurePoint.WriteLine)]
    public void InitializationPropagatesExpectedConsoleFailuresWithoutReadingInput(FailurePoint failurePoint)
    {
        var terminal = new TestSystemTerminal { FailurePoint = failurePoint };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: true);

        Assert.Throws<IOException>(() => driver.TryInitialize());

        Assert.Equal(0, terminal.ReadKeyCount);
    }

    [Fact]
    public void RestoreIsIdempotentAndRestoresTheCapturedCursorState()
    {
        var terminal = new TestSystemTerminal();
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: true);
        driver.TryInitialize();

        driver.Restore();
        driver.Restore();

        Assert.False(terminal.CursorVisible);
        Assert.Equal(2, terminal.CursorWriteCount);
        Assert.Equal(2, terminal.WrittenLines.Count);
    }

    [Fact]
    public void RestoreAbsorbsExpectedConsoleFailures()
    {
        var terminal = new TestSystemTerminal();
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);
        driver.TryInitialize();
        terminal.FailurePoint = FailurePoint.WriteLine;

        driver.Restore();
        driver.Restore();

        Assert.Equal(2, terminal.WriteLineAttemptCount);
    }

    [Fact]
    public void CursorInitializationFailureStillAllowsBestEffortRestore()
    {
        var terminal = new TestSystemTerminal { FailurePoint = FailurePoint.Cursor };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: true);

        Assert.Throws<IOException>(() => driver.TryInitialize());

        terminal.FailurePoint = FailurePoint.None;
        driver.Restore();

        Assert.Single(terminal.WrittenLines);
    }

    [Fact]
    public void RenderNormalizesUntrustedTerminalControlSequences()
    {
        var terminal = new TestSystemTerminal();
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);

        driver.Render(["line\u001B[2J"]);

        Assert.Equal(1, terminal.ClearCount);
        Assert.Equal("line\\u001B[2J", Assert.Single(terminal.WrittenLines));
    }

    [Fact]
    public void PreflightSizeFailureIsVisibleToTheSelector()
    {
        var terminal = new TestSystemTerminal { FailurePoint = FailurePoint.Width };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);

        Assert.Throws<IOException>(() => driver.GetSize());
    }

    [Fact]
    public void SessionSizeFailureKeepsTheLastVerifiedDimensions()
    {
        var terminal = new TestSystemTerminal();
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);
        driver.TryInitialize();
        terminal.FailurePoint = FailurePoint.Width;

        var size = driver.GetSize();

        Assert.Equal(new TerminalSize(80, 20), size);
    }

    [Fact]
    public void TryReadInputDeliversAnAvailableKeyWithInterceptAndWithoutEndOfInput()
    {
        var terminal = new TestSystemTerminal
        {
            NextKey = new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false),
        };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);

        var input = driver.TryReadInput();

        Assert.NotNull(input);
        Assert.False(input!.IsEndOfInput);
        Assert.Equal(ConsoleKey.X, input.Key!.Value.Key);
        Assert.True(terminal.LastReadKeyIntercept);
        Assert.Empty(terminal.WrittenLines);
    }

    [Theory]
    [InlineData(ConsoleKey.C, (char)0x03)]
    [InlineData(ConsoleKey.D, (char)0x04)]
    [InlineData(ConsoleKey.Z, (char)0x1A)]
    public void TryReadInputDeliversControlKeysAsKeyEventsNotEndOfInput(ConsoleKey key, char keyChar)
    {
        var terminal = new TestSystemTerminal
        {
            NextKey = new ConsoleKeyInfo(keyChar, key, false, false, control: true),
        };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);

        var input = driver.TryReadInput();

        Assert.NotNull(input);
        Assert.False(input!.IsEndOfInput);
        Assert.Equal(key, input.Key!.Value.Key);
    }

    [Fact]
    public void TryReadInputReturnsNullWhenNoKeyIsAvailable()
    {
        var terminal = new TestSystemTerminal { KeyAvailableResult = false };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);

        Assert.Null(driver.TryReadInput());
        Assert.Equal(0, terminal.ReadKeyCount);
    }

    [Theory]
    [InlineData(FailurePoint.KeyAvailable, typeof(IOException))]
    [InlineData(FailurePoint.ReadKey, typeof(InvalidOperationException))]
    public void TryReadInputTranslatesExpectedConsoleFailuresToEndOfInput(FailurePoint failurePoint, Type exceptionType)
    {
        var terminal = new TestSystemTerminal
        {
            FailurePoint = failurePoint,
            FailureException = (Exception)Activator.CreateInstance(exceptionType)!,
        };
        var driver = new SystemTerminalDriver(terminal, usesCursorVisibility: false);

        Assert.Same(TerminalInputEvent.EndOfInput, driver.TryReadInput());
    }

    public enum FailurePoint
    {
        None,
        Width,
        Height,
        KeyAvailable,
        Cursor,
        Clear,
        WriteLine,
        ReadKey,
    }

    private sealed class TestSystemTerminal : ISystemTerminal
    {
        private bool _cursorVisible;

        public FailurePoint FailurePoint { get; set; }

        public int WidthReadCount { get; private set; }

        public int HeightReadCount { get; private set; }

        public int KeyAvailableReadCount { get; private set; }

        public int CursorReadCount { get; private set; }

        public int CursorWriteCount { get; private set; }

        public int ReadKeyCount { get; private set; }

        public int ClearCount { get; private set; }

        public int WriteLineAttemptCount { get; private set; }

        public List<string> WrittenLines { get; } = [];

        public ConsoleKeyInfo NextKey { get; set; } = new('a', ConsoleKey.A, false, false, false);

        public bool KeyAvailableResult { get; set; } = true;

        public bool? LastReadKeyIntercept { get; private set; }

        public Exception FailureException { get; set; } = new IOException("configured failure");

        public int WindowWidth
        {
            get
            {
                WidthReadCount++;
                ThrowIfConfigured(FailurePoint.Width);
                return 80;
            }
        }

        public int WindowHeight
        {
            get
            {
                HeightReadCount++;
                ThrowIfConfigured(FailurePoint.Height);
                return 20;
            }
        }

        public bool KeyAvailable
        {
            get
            {
                KeyAvailableReadCount++;
                ThrowIfConfigured(FailurePoint.KeyAvailable);
                return KeyAvailableResult;
            }
        }

        public bool CursorVisible
        {
            get
            {
                CursorReadCount++;
                ThrowIfConfigured(FailurePoint.Cursor);
                return _cursorVisible;
            }
            set
            {
                CursorWriteCount++;
                ThrowIfConfigured(FailurePoint.Cursor);
                _cursorVisible = value;
            }
        }

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            ReadKeyCount++;
            LastReadKeyIntercept = intercept;
            ThrowIfConfigured(FailurePoint.ReadKey);
            return NextKey;
        }

        public void Clear()
        {
            ClearCount++;
            ThrowIfConfigured(FailurePoint.Clear);
        }

        public void WriteLine(string value)
        {
            WriteLineAttemptCount++;
            ThrowIfConfigured(FailurePoint.WriteLine);
            WrittenLines.Add(value);
        }

        private void ThrowIfConfigured(FailurePoint failurePoint)
        {
            if (FailurePoint == failurePoint)
            {
                throw FailureException;
            }
        }
    }
}
