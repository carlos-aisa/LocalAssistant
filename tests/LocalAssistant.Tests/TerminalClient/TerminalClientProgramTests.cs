using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientProgramTests
{
    [Fact]
    public async Task PlainOptionBuildsAndRunsOnlyThePlainApplication()
    {
        var environment = new TestProgramEnvironment();
        var driverFactory = new RecordingDriverFactory();
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync(["--plain"], environment, driverFactory, plain, tui);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, driverFactory.CreateCount);
        Assert.Equal(1, plain.RunCount);
        Assert.Equal(0, tui.RunCount);
        Assert.Empty(environment.Errors);
        Assert.Equal(1, environment.RegisterCount);
        Assert.Equal(1, environment.UnregisterCount);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RedirectionBuildsAndRunsOnlyThePlainApplication(
        bool inputRedirected,
        bool outputRedirected,
        bool errorRedirected)
    {
        var environment = new TestProgramEnvironment(inputRedirected, outputRedirected, errorRedirected);
        var driverFactory = new RecordingDriverFactory();
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync([], environment, driverFactory, plain, tui);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, driverFactory.CreateCount);
        Assert.Equal(1, plain.RunCount);
        Assert.Equal(0, tui.RunCount);
        Assert.Empty(environment.Errors);
    }

    [Fact]
    public async Task PreflightFailureRestoresTheDriverAndRunsOnlyThePlainApplication()
    {
        var environment = new TestProgramEnvironment();
        var driver = new TestDriver(initializes: false);
        var driverFactory = new RecordingDriverFactory(driver);
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync([], environment, driverFactory, plain, tui);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, driverFactory.CreateCount);
        Assert.Equal(1, driver.RestoreCount);
        Assert.Equal(1, plain.RunCount);
        Assert.Equal(0, tui.RunCount);
        Assert.Equal(["TUI unavailable (tui_initialization_failed); using plain mode."], environment.Errors);
    }

    [Fact]
    public async Task CompatiblePreflightRunsOnlyTheTuiWithTheInitializedDriver()
    {
        var environment = new TestProgramEnvironment();
        var driver = new TestDriver();
        var driverFactory = new RecordingDriverFactory(driver);
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync([], environment, driverFactory, plain, tui);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, driverFactory.CreateCount);
        Assert.Equal(1, driver.InitializeCount);
        Assert.Equal(0, plain.RunCount);
        Assert.Equal(1, tui.RunCount);
        Assert.Same(driver, tui.Driver);

        // The composition root keeps an idempotent restoration guard around the whole TUI
        // construction and execution; on the happy path the real host restores first and this
        // guard is a no-op, but the seam still exercises it exactly once.
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public async Task TuiFailureDoesNotStartThePlainRenderer()
    {
        var environment = new TestProgramEnvironment();
        var driver = new TestDriver();
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner(exception: new InvalidOperationException("tui failed"));

        var exitCode = await RunAsync([], environment, new RecordingDriverFactory(driver), plain, tui);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, plain.RunCount);
        Assert.Equal(1, tui.RunCount);
        Assert.Contains(environment.Errors, error => error.Contains("Client error: tui failed", StringComparison.Ordinal));
        Assert.Equal(1, environment.UnregisterCount);
    }

    [Fact]
    public async Task TuiConstructionFailureAfterPreflightRestoresTheDriverOnceWithoutFallingBackToPlain()
    {
        var environment = new TestProgramEnvironment();
        var driver = new TestDriver();
        var plain = new RecordingPlainRunner();

        // A failure while the TUI presentation graph is being built or run, after the preflight
        // has already initialized the driver and before the host's own finally can restore it.
        var tui = new RecordingTuiRunner(exception: new InvalidOperationException("tui graph failed"));

        var exitCode = await RunAsync([], environment, new RecordingDriverFactory(driver), plain, tui);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, tui.RunCount);
        Assert.Equal(0, plain.RunCount);
        Assert.Equal(1, driver.RestoreCount);
        Assert.DoesNotContain(environment.Errors, error => error.Contains("using plain mode", StringComparison.Ordinal));
        Assert.Equal(1, environment.UnregisterCount);
    }

    [Fact]
    public async Task PlainFailureDoesNotStartTheTuiRenderer()
    {
        var environment = new TestProgramEnvironment(inputRedirected: true);
        var plain = new RecordingPlainRunner(exception: new InvalidOperationException("plain failed"));
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync([], environment, new RecordingDriverFactory(), plain, tui);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, plain.RunCount);
        Assert.Equal(0, tui.RunCount);
        Assert.Equal(1, environment.UnregisterCount);
    }

    [Fact]
    public async Task UnexpectedSelectionFailureBuildsNoApplicationAndUnregistersCancellation()
    {
        var environment = new TestProgramEnvironment();
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync(
            [],
            environment,
            new RecordingDriverFactory(exception: new NotSupportedException("unexpected driver failure")),
            plain,
            tui);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, plain.RunCount);
        Assert.Equal(0, tui.RunCount);
        Assert.Equal(1, environment.RegisterCount);
        Assert.Equal(1, environment.UnregisterCount);
    }

    [Fact]
    public async Task FallbackNoticeUsesTheStableReasonInsteadOfTheExceptionMessage()
    {
        var environment = new TestProgramEnvironment();
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner();

        var exitCode = await RunAsync(
            [],
            environment,
            new RecordingDriverFactory(new TestDriver(
                initializationException: new IOException("do not disclose this value"))),
            plain,
            tui);

        Assert.Equal(0, exitCode);
        var error = Assert.Single(environment.Errors);
        Assert.Contains("tui_initialization_failed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("do not disclose this value", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationAfterTuiSelectionIsDeliveredOnlyToTheSelectedRunner()
    {
        var environment = new TestProgramEnvironment();
        var plain = new RecordingPlainRunner();
        var tui = new RecordingTuiRunner(onRun: environment.RaiseCancel);

        var exitCode = await RunAsync([], environment, new RecordingDriverFactory(), plain, tui);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, plain.RunCount);
        Assert.Equal(1, tui.RunCount);
        Assert.True(tui.ReceivedCancellation);
        Assert.Equal(1, environment.UnregisterCount);
    }

    private static Task<int> RunAsync(
        string[] args,
        TestProgramEnvironment environment,
        RecordingDriverFactory driverFactory,
        RecordingPlainRunner plain,
        RecordingTuiRunner tui) => TerminalClientProgram.RunAsync(
            args,
            environment,
            driverFactory.Create,
            plain.RunAsync,
            tui.RunAsync);

    private sealed class TestProgramEnvironment : ITerminalProgramEnvironment
    {
        private Action? _cancellationHandler;

        public TestProgramEnvironment(
            bool inputRedirected = false,
            bool outputRedirected = false,
            bool errorRedirected = false)
        {
            IsInputRedirected = inputRedirected;
            IsOutputRedirected = outputRedirected;
            IsErrorRedirected = errorRedirected;
        }

        public bool IsInputRedirected { get; }

        public bool IsOutputRedirected { get; }

        public bool IsErrorRedirected { get; }

        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public List<string> Errors { get; } = [];

        public void RegisterCancellation(Action cancellationHandler)
        {
            RegisterCount++;
            _cancellationHandler = cancellationHandler;
        }

        public void UnregisterCancellation(Action cancellationHandler)
        {
            UnregisterCount++;
            Assert.Same(_cancellationHandler, cancellationHandler);
            _cancellationHandler = null;
        }

        public void WriteError(string value)
        {
            Errors.Add(value);
        }

        public void RaiseCancel()
        {
            Assert.NotNull(_cancellationHandler);
            _cancellationHandler!();
        }
    }

    private sealed class RecordingDriverFactory
    {
        private readonly ITerminalDriver? _driver;
        private readonly Exception? _exception;

        public RecordingDriverFactory(ITerminalDriver? driver = null, Exception? exception = null)
        {
            _driver = driver ?? new TestDriver();
            _exception = exception;
        }

        public int CreateCount { get; private set; }

        public ITerminalDriver? Create()
        {
            CreateCount++;
            if (_exception is not null)
            {
                throw _exception;
            }

            return _driver;
        }
    }

    private sealed class RecordingPlainRunner
    {
        private readonly Exception? _exception;

        public RecordingPlainRunner(Exception? exception = null)
        {
            _exception = exception;
        }

        public int RunCount { get; private set; }

        public Task<int> RunAsync(TerminalClientOptions options, CancellationToken cancellationToken)
        {
            RunCount++;
            if (_exception is not null)
            {
                return Task.FromException<int>(_exception);
            }

            return Task.FromResult(0);
        }
    }

    private sealed class RecordingTuiRunner
    {
        private readonly Exception? _exception;
        private readonly Action? _onRun;

        public RecordingTuiRunner(Exception? exception = null, Action? onRun = null)
        {
            _exception = exception;
            _onRun = onRun;
        }

        public int RunCount { get; private set; }

        public ITerminalDriver? Driver { get; private set; }

        public bool ReceivedCancellation { get; private set; }

        public Task<int> RunAsync(
            TerminalClientOptions options,
            ITerminalDriver driver,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Driver = driver;
            _onRun?.Invoke();
            ReceivedCancellation = cancellationToken.IsCancellationRequested;
            if (_exception is not null)
            {
                return Task.FromException<int>(_exception);
            }

            return Task.FromResult(0);
        }
    }

    private sealed class TestDriver : ITerminalDriver
    {
        private readonly bool _initializes;
        private readonly Exception? _initializationException;

        public TestDriver(bool initializes = true, Exception? initializationException = null)
        {
            _initializes = initializes;
            _initializationException = initializationException;
        }

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

        public TerminalSize GetSize() => new(80, 20);

        public TerminalInputEvent? TryReadInput() => null;

        public void Render(IReadOnlyList<string> lines)
        {
        }

        public void Restore()
        {
            RestoreCount++;
        }
    }
}
