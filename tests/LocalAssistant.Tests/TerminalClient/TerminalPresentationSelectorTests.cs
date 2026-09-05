using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalPresentationSelectorTests
{
    [Theory]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, false, true, true)]
    public void SelectsTuiOnlyForASuitableInteractiveTerminal(
        bool forcePlain,
        bool inputRedirected,
        bool outputRedirected,
        bool supportsTui,
        bool expected)
    {
        var options = new TerminalClientOptions(
            new Uri("http://localhost:5100/"),
            "fake",
            "direct",
            forcePlain);
        var capabilities = new TestCapabilities(inputRedirected, outputRedirected, supportsTui);

        var selected = TerminalPresentationSelector.UseTui(options, capabilities);

        Assert.Equal(expected, selected);
    }

    private sealed class TestCapabilities : ITerminalPresentationCapabilities
    {
        public TestCapabilities(bool inputRedirected, bool outputRedirected, bool supportsInteractiveTui)
        {
            IsInputRedirected = inputRedirected;
            IsOutputRedirected = outputRedirected;
            SupportsInteractiveTui = supportsInteractiveTui;
        }

        public bool IsInputRedirected { get; }

        public bool IsOutputRedirected { get; }

        public bool SupportsInteractiveTui { get; }
    }
}
