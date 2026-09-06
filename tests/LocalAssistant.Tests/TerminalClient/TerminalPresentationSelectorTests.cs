using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalPresentationSelectorTests
{
    [Theory]
    [InlineData(true, false, false, true, 80, 20, false, "plain_requested")]
    [InlineData(false, true, false, true, 80, 20, false, "redirected")]
    [InlineData(false, false, true, true, 80, 20, false, "redirected")]
    [InlineData(false, false, false, false, 80, 20, false, "unsupported_terminal")]
    [InlineData(false, false, false, true, 39, 8, false, "terminal_too_small")]
    [InlineData(false, false, false, true, 40, 7, false, "terminal_too_small")]
    [InlineData(false, false, false, true, 40, 8, true, "interactive_terminal")]
    public void SelectsThePresentationBeforeTheApplicationStarts(
        bool forcePlain,
        bool inputRedirected,
        bool outputRedirected,
        bool supportsTui,
        int width,
        int height,
        bool expectsTui,
        string expectedReason)
    {
        var options = new TerminalClientOptions(
            new Uri("http://localhost:5100/"),
            "fake",
            "direct",
            forcePlain);
        var capabilities = new TestCapabilities(inputRedirected, outputRedirected, supportsTui);
        var driver = new TestTerminalDriver(new TerminalSize(width, height));

        var decision = TerminalPresentationSelector.Select(options, capabilities, driver);

        Assert.Equal(expectedReason, decision.Reason);
        Assert.Equal(expectsTui, decision.UsesTui);
    }

    [Fact]
    public void InitializationFailureFallsBackToPlain()
    {
        var options = new TerminalClientOptions(new Uri("http://localhost:5100/"), "fake", "direct");
        var driver = new TestTerminalDriver(new TerminalSize(80, 20), initializes: false);

        var decision = TerminalPresentationSelector.Select(options, new TestCapabilities(), driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("tui_initialization_failed", decision.Reason);
        Assert.True(driver.Initialized);
        Assert.True(driver.Restored);
    }

    [Fact]
    public void ExpectedInitializationExceptionFallsBackToPlainAndRestoresTheDriver()
    {
        var options = new TerminalClientOptions(new Uri("http://localhost:5100/"), "fake", "direct");
        var driver = new TestTerminalDriver(
            new TerminalSize(80, 20),
            initializationException: new IOException("unavailable"));

        var decision = TerminalPresentationSelector.Select(options, new TestCapabilities(), driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("tui_initialization_failed", decision.Reason);
        Assert.True(driver.Restored);
    }

    private sealed class TestCapabilities : ITerminalPresentationCapabilities
    {
        public TestCapabilities(
            bool isInputRedirected = false,
            bool isOutputRedirected = false,
            bool supportsInteractiveTui = true)
        {
            IsInputRedirected = isInputRedirected;
            IsOutputRedirected = isOutputRedirected;
            SupportsInteractiveTui = supportsInteractiveTui;
        }

        public bool IsInputRedirected { get; }

        public bool IsOutputRedirected { get; }

        public bool SupportsInteractiveTui { get; }
    }

    private sealed class TestTerminalDriver : ITerminalDriver
    {
        private readonly bool _initializes;
        private readonly Exception? _initializationException;

        public TestTerminalDriver(
            TerminalSize size,
            bool initializes = true,
            Exception? initializationException = null)
        {
            Size = size;
            _initializes = initializes;
            _initializationException = initializationException;
        }

        public TerminalSize Size { get; }

        public bool Initialized { get; private set; }

        public bool Restored { get; private set; }

        public bool TryInitialize()
        {
            Initialized = true;
            if (_initializationException is not null)
            {
                throw _initializationException;
            }

            return _initializes;
        }

        public TerminalSize GetSize() => Size;

        public TerminalInputEvent? TryReadInput() => null;

        public void Render(IReadOnlyList<string> lines)
        {
        }

        public void Restore()
        {
            Restored = true;
        }
    }
}
