using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalDiagnosticsTests
{
    [Fact]
    public async Task ReportIncludesEverySectionWithoutSensitiveContent()
    {
        var report = await BuildAsync(
            provider: "ollama",
            apiHealth: ApiProbe(TerminalDiagnosticsApiHealth.Reachable, 200),
            localState: LocalState(new TerminalDiagnosticsLocalStateSection(
                @"C:\state\private-client.json",
                FileExists: true,
                IsReadable: true,
                SchemaVersion: 2,
                ClientId: "client-a",
                HasLastConversationId: true)),
            spokenOutput: () => new TerminalDiagnosticsSpokenOutputSection(true, true, 3, true));

        var text = report.ToText();

        Assert.Contains("Client:", text, StringComparison.Ordinal);
        Assert.Contains("Environment:", text, StringComparison.Ordinal);
        Assert.Contains("Configuration:", text, StringComparison.Ordinal);
        Assert.Contains("API:", text, StringComparison.Ordinal);
        Assert.Contains("Local state", text, StringComparison.Ordinal);
        Assert.Contains("Bearer:", text, StringComparison.Ordinal);
        Assert.Contains("Presentation:", text, StringComparison.Ordinal);
        Assert.Contains("Spoken output:", text, StringComparison.Ordinal);

        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer-token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("challenge", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Zira", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Helena", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BearerIsAlwaysReportedAsNotPersisted()
    {
        var report = await BuildAsync();

        Assert.Contains("not persisted", report.BearerStatus, StringComparison.Ordinal);
    }

    [Fact]
    public Task ReachableHealthCarriesNoGuidance() =>
        AssertHealthClassification(TerminalDiagnosticsApiHealth.Reachable, 200, expectsGuidance: false);

    [Fact]
    public Task UnreachableHealthCarriesGuidance() =>
        AssertHealthClassification(TerminalDiagnosticsApiHealth.Unreachable, null, expectsGuidance: true);

    [Fact]
    public Task TimeoutHealthCarriesGuidance() =>
        AssertHealthClassification(TerminalDiagnosticsApiHealth.Timeout, null, expectsGuidance: true);

    [Fact]
    public Task UnexpectedStatusHealthCarriesGuidance() =>
        AssertHealthClassification(TerminalDiagnosticsApiHealth.UnexpectedStatus, 503, expectsGuidance: true);

    private static async Task AssertHealthClassification(
        TerminalDiagnosticsApiHealth health,
        int? statusCode,
        bool expectsGuidance)
    {
        var report = await BuildAsync(apiHealth: ApiProbe(health, statusCode));

        Assert.Equal(health, report.Api.Health);
        Assert.Equal(statusCode, report.Api.StatusCode);

        var text = report.ToText();
        Assert.Equal(expectsGuidance, text.Contains("the client never starts it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedStatusReportsTheNumericCode()
    {
        var report = await BuildAsync(apiHealth: ApiProbe(TerminalDiagnosticsApiHealth.UnexpectedStatus, 503));

        Assert.Contains("503", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLocalStateFileIsReportedWithoutSchemaOrIdentity()
    {
        var path = @"C:\state\private-client.json";
        var report = await BuildAsync(
            localState: LocalState(TerminalDiagnosticsLocalStateSection.Missing(path)));

        Assert.False(report.LocalState.FileExists);
        Assert.False(report.LocalState.IsReadable);
        Assert.Null(report.LocalState.SchemaVersion);
        Assert.Null(report.LocalState.ClientId);
        Assert.False(report.LocalState.HasLastConversationId);
        Assert.Contains("not found", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableLocalStateFileIsReportedWithoutSchemaOrIdentity()
    {
        var report = await BuildAsync(
            localState: LocalState(new TerminalDiagnosticsLocalStateSection(
                @"C:\state\private-client.json",
                FileExists: true,
                IsReadable: false,
                SchemaVersion: null,
                ClientId: null,
                HasLastConversationId: false)));

        Assert.Contains("unreadable", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyLocalStateFileWithNoLastConversationIdIsReportedHonestly()
    {
        var report = await BuildAsync(
            localState: LocalState(new TerminalDiagnosticsLocalStateSection(
                @"C:\state\private-client.json",
                FileExists: true,
                IsReadable: true,
                SchemaVersion: null,
                ClientId: "client-legacy",
                HasLastConversationId: false)));

        var text = report.ToText();
        Assert.Contains("legacy", text, StringComparison.Ordinal);
        Assert.Contains("client-legacy", text, StringComparison.Ordinal);
        Assert.Contains("last conversation id present: no", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationSectionReportsTheEffectiveValueAndOriginOfEveryKey()
    {
        var configuration = new TerminalClientConfigurationResult(
            new TerminalClientOptions(
                new Uri("http://localhost:6100/"),
                "fake",
                "time",
                TimeSpan.FromMinutes(2),
                ForcePlain: false),
            new Dictionary<string, TerminalClientSettingOrigin>(StringComparer.Ordinal)
            {
                ["BaseUrl"] = TerminalClientSettingOrigin.AppSettings,
                ["Provider"] = TerminalClientSettingOrigin.EnvironmentVariable,
                ["Scenario"] = TerminalClientSettingOrigin.CommandLine,
                ["RequestTimeout"] = TerminalClientSettingOrigin.Default,
            });

        var report = await BuildAsync(configuration);

        Assert.Equal(4, report.Configuration.Settings.Count);
        Assert.Contains(
            report.Configuration.Settings,
            setting => setting.Key == "BaseUrl" && setting.Origin == TerminalClientSettingOrigin.AppSettings);
        Assert.Contains(
            report.Configuration.Settings,
            setting => setting.Key == "Provider" && setting.Origin == TerminalClientSettingOrigin.EnvironmentVariable);
        Assert.Contains(
            report.Configuration.Settings,
            setting => setting.Key == "Scenario" && setting.Origin == TerminalClientSettingOrigin.CommandLine);
        Assert.Contains(
            report.Configuration.Settings,
            setting => setting.Key == "RequestTimeout" && setting.Origin == TerminalClientSettingOrigin.Default);

        var text = report.ToText();
        Assert.Contains("[appsettings.json]", text, StringComparison.Ordinal);
        Assert.Contains("[environment variable]", text, StringComparison.Ordinal);
        Assert.Contains("[command line]", text, StringComparison.Ordinal);
        Assert.Contains("[default]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresentationReflectsARedirectedEnvironmentWithoutTouchingAnyDriver()
    {
        var driverFactory = new RecordingDriverFactory(exception: new InvalidOperationException("must not be built"));

        var report = await BuildAsync(
            capabilities: new FakeCapabilities(inputRedirected: true),
            driverFactory: driverFactory.Create);

        Assert.False(report.Presentation.WillUseTui);
        Assert.Equal("redirected", report.Presentation.Reason);
        Assert.Equal(0, driverFactory.CreateCount);
    }

    [Fact]
    public async Task PresentationReflectsAnInteractiveTerminalAndRestoresTheDriverItInitialized()
    {
        var driver = new FakeDriver();
        var driverFactory = new RecordingDriverFactory(driver);

        var report = await BuildAsync(
            capabilities: new FakeCapabilities(inputRedirected: false, outputRedirected: false, errorRedirected: false),
            driverFactory: driverFactory.Create);

        Assert.True(report.Presentation.WillUseTui);
        Assert.Equal(1, driverFactory.CreateCount);
        Assert.Equal(1, driver.RestoreCount);
    }

    [Fact]
    public async Task SpokenOutputOutsideWindowsNeverReportsAVoiceCount()
    {
        var report = await BuildAsync(spokenOutput: () => TerminalDiagnosticsSpokenOutputSection.NotWindows);

        Assert.False(report.SpokenOutput.IsWindows);
        Assert.Null(report.SpokenOutput.EnabledVoiceCount);
        var text = report.ToText();
        Assert.Contains("not Windows", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Enabled voices", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpokenOutputOnWindowsReportsOnlyTheVoiceCountNeverNames()
    {
        var report = await BuildAsync(spokenOutput: () => new TerminalDiagnosticsSpokenOutputSection(true, true, 5, true));

        Assert.Equal(5, report.SpokenOutput.EnabledVoiceCount);
        Assert.Contains("Enabled voices: 5", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsyncRejectsNullArguments()
    {
        var configuration = Configuration();
        await Assert.ThrowsAsync<ArgumentNullException>(() => TerminalDiagnosticsProbe.BuildAsync(
            null!,
            "appsettings.json",
            false,
            ApiProbe(TerminalDiagnosticsApiHealth.Reachable),
            LocalState(TerminalDiagnosticsLocalStateSection.Missing("path")),
            new FakeCapabilities(),
            () => null,
            () => TerminalDiagnosticsSpokenOutputSection.NotWindows,
            CancellationToken.None));
    }

    private static TerminalClientConfigurationResult Configuration(string provider = "fake") =>
        new(
            new TerminalClientOptions(
                new Uri("http://localhost:5100/"),
                provider,
                "direct",
                TerminalClientOptions.DefaultRequestTimeout,
                ForcePlain: false),
            new Dictionary<string, TerminalClientSettingOrigin>(StringComparer.Ordinal)
            {
                ["BaseUrl"] = TerminalClientSettingOrigin.Default,
                ["Provider"] = TerminalClientSettingOrigin.Default,
                ["Scenario"] = TerminalClientSettingOrigin.Default,
                ["RequestTimeout"] = TerminalClientSettingOrigin.Default,
            });

    private static Func<CancellationToken, Task<TerminalDiagnosticsApiProbeResult>> ApiProbe(
        TerminalDiagnosticsApiHealth health,
        int? statusCode = null) =>
        _ => Task.FromResult(new TerminalDiagnosticsApiProbeResult(health, statusCode));

    private static Func<CancellationToken, Task<TerminalDiagnosticsLocalStateSection>> LocalState(
        TerminalDiagnosticsLocalStateSection section) =>
        _ => Task.FromResult(section);

    private static Task<TerminalDiagnosticsReport> BuildAsync(
        TerminalClientConfigurationResult? configuration = null,
        string provider = "fake",
        Func<CancellationToken, Task<TerminalDiagnosticsApiProbeResult>>? apiHealth = null,
        Func<CancellationToken, Task<TerminalDiagnosticsLocalStateSection>>? localState = null,
        ITerminalPresentationCapabilities? capabilities = null,
        Func<ITerminalDriver?>? driverFactory = null,
        Func<TerminalDiagnosticsSpokenOutputSection>? spokenOutput = null) =>
        TerminalDiagnosticsProbe.BuildAsync(
            configuration ?? Configuration(provider),
            @"C:\client\appsettings.json",
            appSettingsFileExists: false,
            apiHealth ?? ApiProbe(TerminalDiagnosticsApiHealth.Reachable, 200),
            localState ?? LocalState(TerminalDiagnosticsLocalStateSection.Missing(@"C:\state\private-client.json")),
            capabilities ?? new FakeCapabilities(),
            driverFactory ?? (() => null),
            spokenOutput ?? (() => TerminalDiagnosticsSpokenOutputSection.NotWindows),
            CancellationToken.None);

    private sealed class FakeCapabilities : ITerminalPresentationCapabilities
    {
        public FakeCapabilities(
            bool inputRedirected = true,
            bool outputRedirected = true,
            bool errorRedirected = true)
        {
            IsInputRedirected = inputRedirected;
            IsOutputRedirected = outputRedirected;
            IsErrorRedirected = errorRedirected;
        }

        public bool IsInputRedirected { get; }

        public bool IsOutputRedirected { get; }

        public bool IsErrorRedirected { get; }
    }

    private sealed class FakeDriver : ITerminalDriver
    {
        public int RestoreCount { get; private set; }

        public bool TryInitialize() => true;

        public TerminalSize GetSize() => new(80, 24);

        public TerminalInputEvent? TryReadInput() => null;

        public void Render(IReadOnlyList<string> lines)
        {
        }

        public void Restore() => RestoreCount++;
    }

    private sealed class RecordingDriverFactory
    {
        private readonly ITerminalDriver? _driver;
        private readonly Exception? _exception;

        public RecordingDriverFactory(ITerminalDriver? driver = null, Exception? exception = null)
        {
            _driver = driver;
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
}
