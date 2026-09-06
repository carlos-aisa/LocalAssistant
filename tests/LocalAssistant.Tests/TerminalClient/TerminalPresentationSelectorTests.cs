using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalPresentationSelectorTests
{
    [Theory]
    [InlineData(true, false, false, false, "plain_requested")]
    [InlineData(false, true, false, false, "redirected")]
    [InlineData(false, false, true, false, "redirected")]
    [InlineData(false, false, false, true, "redirected")]
    public void ExplicitPlainOrRedirectionDoesNotCreateTheDriver(
        bool forcePlain,
        bool inputRedirected,
        bool outputRedirected,
        bool errorRedirected,
        string expectedReason)
    {
        var factory = new RecordingDriverFactory(new TestTerminalDriver(new TerminalSize(80, 20)));

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(forcePlain),
            new TestCapabilities(inputRedirected, outputRedirected, errorRedirected),
            factory.Create);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal(expectedReason, decision.Reason);
        Assert.Null(decision.Driver);
        Assert.Equal(0, factory.CreateCount);
    }

    [Theory]
    [InlineData(39, 8)]
    [InlineData(40, 7)]
    public void SmallTerminalFallsBackAndRestoresOnce(int width, int height)
    {
        var driver = new TestTerminalDriver(new TerminalSize(width, height));

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            () => driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("terminal_too_small", decision.Reason);
        Assert.Null(decision.Driver);
        Assert.Equal(1, driver.InitializeCount);
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public void CompatibleTerminalTransfersTheInitializedDriverWithoutRestoring()
    {
        var driver = new TestTerminalDriver(new TerminalSize(40, 8));

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            () => driver);

        Assert.True(decision.UsesTui);
        Assert.Equal("interactive_terminal", decision.Reason);
        Assert.Same(driver, decision.Driver);
        Assert.Equal(1, driver.InitializeCount);
        Assert.Equal(0, driver.RestoreCount);
    }

    [Fact]
    public void MissingDriverFallsBackWithoutAttemptingInitialization()
    {
        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            static () => null);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("unsupported_terminal", decision.Reason);
        Assert.Null(decision.Driver);
    }

    [Fact]
    public void FalseInitializationFallsBackAndRestoresOnce()
    {
        var driver = new TestTerminalDriver(new TerminalSize(80, 20), initializes: false);

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            () => driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("tui_initialization_failed", decision.Reason);
        Assert.Null(decision.Driver);
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public void ExpectedRestoreFailureDoesNotPreventThePlainFallback()
    {
        var driver = new TestTerminalDriver(
            new TerminalSize(80, 20),
            initializes: false,
            restoreException: new IOException("restore unavailable"));

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            () => driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("tui_initialization_failed", decision.Reason);
        Assert.Equal(1, driver.RestoreCount);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    public void ExpectedInitializationExceptionFallsBackAndRestoresOnce(Type exceptionType)
    {
        var driver = new TestTerminalDriver(
            new TerminalSize(80, 20),
            initializationException: (Exception)Activator.CreateInstance(exceptionType)!);

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            () => driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("tui_initialization_failed", decision.Reason);
        Assert.Null(decision.Driver);
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public void ExpectedSizeExceptionFallsBackAndRestoresOnce()
    {
        var driver = new TestTerminalDriver(
            new TerminalSize(80, 20),
            sizeException: new IOException("unavailable"));

        var decision = TerminalPresentationSelector.Select(
            CreateOptions(),
            new TestCapabilities(),
            () => driver);

        Assert.Equal(TerminalPresentationMode.Plain, decision.Mode);
        Assert.Equal("tui_initialization_failed", decision.Reason);
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public void UnexpectedInitializationExceptionRestoresOnceAndPropagatesWithoutReclassification()
    {
        var driver = new TestTerminalDriver(
            new TerminalSize(80, 20),
            initializationException: new NotSupportedException("unexpected"));

        var exception = Assert.Throws<NotSupportedException>(() =>
            TerminalPresentationSelector.Select(
                CreateOptions(),
                new TestCapabilities(),
                () => driver));

        Assert.Equal("unexpected", exception.Message);

        // The failed preparation still requests restoration exactly once: TryInitialize may
        // already have hidden the cursor or cleared the screen before it threw.
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public void UnexpectedExceptionBeforeTheDriverExistsPropagatesUntouched()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            TerminalPresentationSelector.Select(
                CreateOptions(),
                new TestCapabilities(),
                static ITerminalDriver? () => throw new NotSupportedException("factory unavailable")));

        Assert.Equal("factory unavailable", exception.Message);
    }

    [Fact]
    public void TuiDecisionCannotBeCreatedWithoutADriver()
    {
        Assert.Throws<ArgumentNullException>(() => TerminalPresentationDecision.Tui(null!));
    }

    private static TerminalClientOptions CreateOptions(bool forcePlain = false) => new(
        new Uri("http://localhost:5100/"),
        "fake",
        "direct",
        forcePlain);

    private sealed class TestCapabilities : ITerminalPresentationCapabilities
    {
        public TestCapabilities(
            bool isInputRedirected = false,
            bool isOutputRedirected = false,
            bool isErrorRedirected = false)
        {
            IsInputRedirected = isInputRedirected;
            IsOutputRedirected = isOutputRedirected;
            IsErrorRedirected = isErrorRedirected;
        }

        public bool IsInputRedirected { get; }

        public bool IsOutputRedirected { get; }

        public bool IsErrorRedirected { get; }
    }

    private sealed class RecordingDriverFactory
    {
        private readonly ITerminalDriver _driver;

        public RecordingDriverFactory(ITerminalDriver driver)
        {
            _driver = driver;
        }

        public int CreateCount { get; private set; }

        public ITerminalDriver Create()
        {
            CreateCount++;
            return _driver;
        }
    }

    private sealed class TestTerminalDriver : ITerminalDriver
    {
        private readonly bool _initializes;
        private readonly Exception? _initializationException;
        private readonly Exception? _sizeException;
        private readonly Exception? _restoreException;

        public TestTerminalDriver(
            TerminalSize size,
            bool initializes = true,
            Exception? initializationException = null,
            Exception? sizeException = null,
            Exception? restoreException = null)
        {
            Size = size;
            _initializes = initializes;
            _initializationException = initializationException;
            _sizeException = sizeException;
            _restoreException = restoreException;
        }

        public TerminalSize Size { get; }

        public int InitializeCount { get; private set; }

        public int RestoreCount { get; private set; }

        public bool TryInitialize()
        {
            InitializeCount++;
            if (_initializationException is not null)
            {
                throw _initializationException;
            }

            return _initializes;
        }

        public TerminalSize GetSize()
        {
            if (_sizeException is not null)
            {
                throw _sizeException;
            }

            return Size;
        }

        public TerminalInputEvent? TryReadInput() => null;

        public void Render(IReadOnlyList<string> lines)
        {
        }

        public void Restore()
        {
            RestoreCount++;
            if (_restoreException is not null)
            {
                throw _restoreException;
            }
        }
    }
}
