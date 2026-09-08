using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalKeyInterpreterTests
{
    private const char Escape = (char)0x1B;
    private const char EndOfTransmission = (char)0x04;
    private const char Substitute = (char)0x1A;
    private const char EndOfText = (char)0x03;

    [Theory]
    [InlineData('a')]
    [InlineData('ñ')]
    [InlineData('á')]
    [InlineData('¿')]
    public void PrintableCharacterWithAnActivePromptInserts(char character)
    {
        var intent = Interpret(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false), inputActive: true);

        Assert.Equal(TerminalKeyAction.Insert, intent.Action);
        Assert.Equal(character, intent.Character);
    }

    [Fact]
    public void BackspaceWithAnActivePromptAndTextDeletesThePreviousCharacter()
    {
        var intent = Interpret(Key(ConsoleKey.Backspace), inputActive: true, bufferHasText: true);

        Assert.Equal(TerminalKeyAction.DeletePrevious, intent.Action);
    }

    [Fact]
    public void BackspaceWithAnActivePromptAndNoTextIsIgnored()
    {
        var intent = Interpret(Key(ConsoleKey.Backspace), inputActive: true, bufferHasText: false);

        Assert.Equal(TerminalKeyAction.Ignore, intent.Action);
    }

    [Theory]
    [InlineData('a', ConsoleKey.A)]
    [InlineData('\b', ConsoleKey.Backspace)]
    [InlineData('\r', ConsoleKey.Enter)]
    [InlineData(Escape, ConsoleKey.Escape)]
    public void BufferKeysWithoutAnActivePromptAreIgnored(char character, ConsoleKey key)
    {
        var intent = Interpret(
            new ConsoleKeyInfo(character, key, false, false, false),
            inputActive: false,
            bufferHasText: true);

        Assert.Equal(TerminalKeyAction.Ignore, intent.Action);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PageUpAndPageDownScrollRegardlessOfAnActivePrompt(bool inputActive)
    {
        var up = Interpret(Key(ConsoleKey.PageUp), inputActive);
        var down = Interpret(Key(ConsoleKey.PageDown), inputActive);

        Assert.Equal(TerminalKeyAction.Scroll, up.Action);
        Assert.Equal(TerminalKeyInterpreter.PageScrollLines, up.ScrollDelta);
        Assert.Equal(TerminalKeyAction.Scroll, down.Action);
        Assert.Equal(-TerminalKeyInterpreter.PageScrollLines, down.ScrollDelta);
    }

    [Theory]
    [InlineData(ConsoleKey.UpArrow)]
    [InlineData(ConsoleKey.DownArrow)]
    [InlineData(ConsoleKey.Tab)]
    [InlineData(ConsoleKey.F1)]
    public void ReservedAndUnassignedKeysAreIgnored(ConsoleKey key)
    {
        var intent = Interpret(Key(key), inputActive: true, bufferHasText: true);

        Assert.Equal(TerminalKeyAction.Ignore, intent.Action);
    }

    [Fact]
    public void EnterWithAnActivePromptSubmits()
    {
        var intent = Interpret(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), inputActive: true);

        Assert.Equal(TerminalKeyAction.Submit, intent.Action);
    }

    [Fact]
    public void EscapeWithAnActivePromptSubmitsAnEmptyValueAndNeverClosesTheChannel()
    {
        var intent = Interpret(new ConsoleKeyInfo(Escape, ConsoleKey.Escape, false, false, false), inputActive: true);

        Assert.Equal(TerminalKeyAction.SubmitEmpty, intent.Action);
    }

    [Theory]
    [InlineData(ConsoleKey.D, EndOfTransmission)]
    [InlineData(ConsoleKey.Z, Substitute)]
    public void ControlDOrZWithAnEmptyBufferClosesTheChannel(ConsoleKey key, char character)
    {
        var intent = Interpret(
            new ConsoleKeyInfo(character, key, false, false, control: true),
            inputActive: true,
            bufferHasText: false);

        Assert.Equal(TerminalKeyAction.CloseChannel, intent.Action);
    }

    [Theory]
    [InlineData(ConsoleKey.D, EndOfTransmission)]
    [InlineData(ConsoleKey.Z, Substitute)]
    public void ControlDOrZWithTextInTheBufferIsIgnored(ConsoleKey key, char character)
    {
        var intent = Interpret(
            new ConsoleKeyInfo(character, key, false, false, control: true),
            inputActive: true,
            bufferHasText: true);

        Assert.Equal(TerminalKeyAction.Ignore, intent.Action);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ControlCAlwaysCancelsTheApplication(bool inputActive, bool bufferHasText)
    {
        var intent = Interpret(
            new ConsoleKeyInfo(EndOfText, ConsoleKey.C, false, false, control: true),
            inputActive,
            bufferHasText);

        Assert.Equal(TerminalKeyAction.CancelApplication, intent.Action);
    }

    [Fact]
    public void OnlyEnterWithAnActivePromptProducesASubmit()
    {
        var keys = new[]
        {
            new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false),
            new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
            new ConsoleKeyInfo(Escape, ConsoleKey.Escape, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false),
            new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
            new ConsoleKeyInfo(EndOfText, ConsoleKey.C, false, false, true),
            new ConsoleKeyInfo(EndOfTransmission, ConsoleKey.D, false, false, true),
            new ConsoleKeyInfo(Substitute, ConsoleKey.Z, false, false, true),
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
        };

        foreach (var key in keys)
        {
            foreach (var inputActive in new[] { true, false })
            {
                foreach (var bufferHasText in new[] { true, false })
                {
                    Assert.NotEqual(
                        TerminalKeyAction.Submit,
                        TerminalKeyInterpreter.Interpret(key, inputActive, bufferHasText).Action);
                }
            }
        }
    }

    private static TerminalKeyIntent Interpret(ConsoleKeyInfo key, bool inputActive, bool bufferHasText = false) =>
        TerminalKeyInterpreter.Interpret(key, inputActive, bufferHasText);

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
}
