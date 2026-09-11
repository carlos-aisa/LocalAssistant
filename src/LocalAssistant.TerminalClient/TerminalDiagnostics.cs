using System.Globalization;
using System.Runtime.InteropServices;

namespace LocalAssistant.TerminalClient;

internal enum TerminalDiagnosticsApiHealth
{
    Reachable,
    Unreachable,
    Timeout,
    UnexpectedStatus,
}

internal sealed record TerminalDiagnosticsApiProbeResult(TerminalDiagnosticsApiHealth Health, int? StatusCode);

internal sealed record TerminalDiagnosticsClientSection(string Name, string Version);

internal sealed record TerminalDiagnosticsEnvironmentSection(
    string FrameworkDescription,
    string OperatingSystemDescription,
    string ProcessArchitecture,
    string CultureName);

internal sealed record TerminalDiagnosticsSettingValue(
    string Key,
    string Value,
    TerminalClientSettingOrigin Origin);

internal sealed record TerminalDiagnosticsConfigurationSection(
    string AppSettingsPath,
    bool AppSettingsFileExists,
    IReadOnlyList<TerminalDiagnosticsSettingValue> Settings);

internal sealed record TerminalDiagnosticsApiSection(
    Uri BaseUri,
    bool IsLoopback,
    TerminalDiagnosticsApiHealth Health,
    int? StatusCode);

internal sealed record TerminalDiagnosticsLocalStateSection(
    string Path,
    bool FileExists,
    bool IsReadable,
    int? SchemaVersion,
    string? ClientId,
    bool HasLastConversationId)
{
    public static TerminalDiagnosticsLocalStateSection Missing(string path) =>
        new(path, FileExists: false, IsReadable: false, SchemaVersion: null, ClientId: null, HasLastConversationId: false);
}

internal sealed record TerminalDiagnosticsPresentationSection(bool WillUseTui, string Reason);

internal sealed record TerminalDiagnosticsSpokenOutputSection(
    bool IsWindows,
    bool SubsystemInitializes,
    int? EnabledVoiceCount,
    bool IsAvailable)
{
    public static TerminalDiagnosticsSpokenOutputSection NotWindows { get; } =
        new(IsWindows: false, SubsystemInitializes: false, EnabledVoiceCount: null, IsAvailable: false);
}

/// <summary>
/// The redacted, read-only report printed by <c>--diagnostics</c>. Never carries the
/// bearer, a credential, a challenge, spoken text, audio or an installed voice's name;
/// the spoken-output section reports only a count. Building this record performs no
/// side effect of its own — every probe is supplied by the caller.
/// </summary>
internal sealed record TerminalDiagnosticsReport(
    TerminalDiagnosticsClientSection Client,
    TerminalDiagnosticsEnvironmentSection Environment,
    TerminalDiagnosticsConfigurationSection Configuration,
    TerminalDiagnosticsApiSection Api,
    TerminalDiagnosticsLocalStateSection LocalState,
    string BearerStatus,
    TerminalDiagnosticsPresentationSection Presentation,
    TerminalDiagnosticsSpokenOutputSection SpokenOutput)
{
    private const string ApiUnavailableGuidance =
        "The API is not responding at this address. Start it manually before using the " +
        "client; the client never starts it.";

    public string ToText()
    {
        List<string> lines =
        [
            "LocalAssistant.TerminalClient diagnostics",
            string.Empty,
            "Client:",
            $"  Version: {Client.Version}",
            string.Empty,
            "Environment:",
            $"  Runtime: {Environment.FrameworkDescription}",
            $"  OS: {Environment.OperatingSystemDescription}",
            $"  Architecture: {Environment.ProcessArchitecture}",
            $"  Culture: {Environment.CultureName}",
            string.Empty,
            "Configuration:",
            $"  appsettings.json: {Configuration.AppSettingsPath} " +
                (Configuration.AppSettingsFileExists ? "(found)" : "(not found)"),
        ];

        foreach (var setting in Configuration.Settings)
        {
            lines.Add($"  {setting.Key} = {setting.Value} [{FormatOrigin(setting.Origin)}]");
        }

        lines.Add(string.Empty);
        lines.Add("API:");
        lines.Add($"  Base URL: {Api.BaseUri} ({(Api.IsLoopback ? "loopback" : "not loopback")})");
        lines.Add($"  Health: {FormatHealth(Api.Health, Api.StatusCode)}");
        if (Api.Health != TerminalDiagnosticsApiHealth.Reachable)
        {
            lines.Add($"  {ApiUnavailableGuidance}");
        }

        lines.Add(string.Empty);
        lines.Add("Local state (private-client.json):");
        lines.Add($"  Path: {LocalState.Path}");
        lines.Add($"  File: {FormatLocalState(LocalState)}");

        lines.Add(string.Empty);
        lines.Add($"Bearer: {BearerStatus}");

        lines.Add(string.Empty);
        lines.Add("Presentation:");
        lines.Add($"  Would use: {(Presentation.WillUseTui ? "tui" : "plain")} ({Presentation.Reason})");

        lines.Add(string.Empty);
        lines.Add("Spoken output:");
        lines.Add($"  Platform: {(SpokenOutput.IsWindows ? "Windows" : "not Windows")}");
        if (SpokenOutput.IsWindows)
        {
            lines.Add($"  Subsystem initializes: {(SpokenOutput.SubsystemInitializes ? "yes" : "no")}");
            lines.Add(
                $"  Enabled voices: {SpokenOutput.EnabledVoiceCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        }

        lines.Add($"  Availability: {(SpokenOutput.IsAvailable ? "ready" : "unavailable")}");

        return string.Join(System.Environment.NewLine, lines);
    }

    private static string FormatOrigin(TerminalClientSettingOrigin origin) => origin switch
    {
        TerminalClientSettingOrigin.CommandLine => "command line",
        TerminalClientSettingOrigin.EnvironmentVariable => "environment variable",
        TerminalClientSettingOrigin.AppSettings => "appsettings.json",
        _ => "default",
    };

    private static string FormatHealth(TerminalDiagnosticsApiHealth health, int? statusCode) => health switch
    {
        TerminalDiagnosticsApiHealth.Reachable => "reachable",
        TerminalDiagnosticsApiHealth.Timeout => "timeout",
        TerminalDiagnosticsApiHealth.UnexpectedStatus =>
            $"unexpected status {statusCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
        _ => "unreachable",
    };

    private static string FormatLocalState(TerminalDiagnosticsLocalStateSection state)
    {
        if (!state.FileExists)
        {
            return "not found";
        }

        if (!state.IsReadable)
        {
            return "found (unreadable or corrupt)";
        }

        var schemaVersion = state.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "legacy (pre-schema)";
        var clientId = string.IsNullOrWhiteSpace(state.ClientId) ? "none" : state.ClientId;
        var lastConversation = state.HasLastConversationId ? "yes" : "no";
        return $"found; format: {schemaVersion}; client id: {clientId}; last conversation id present: {lastConversation}";
    }
}

/// <summary>
/// Assembles a <see cref="TerminalDiagnosticsReport"/> from already-resolved
/// configuration plus a small set of injected, already-typed probes. Performs no I/O
/// of its own beyond composing what the probes and the reused
/// <see cref="TerminalPresentationSelector"/> return, so tests can drive every branch
/// with deterministic doubles.
/// </summary>
internal static class TerminalDiagnosticsProbe
{
    public static readonly TimeSpan ApiProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<TerminalDiagnosticsReport> BuildAsync(
        TerminalClientConfigurationResult configuration,
        string appSettingsPath,
        bool appSettingsFileExists,
        Func<CancellationToken, Task<TerminalDiagnosticsApiProbeResult>> probeApiHealthAsync,
        Func<CancellationToken, Task<TerminalDiagnosticsLocalStateSection>> readLocalStateAsync,
        ITerminalPresentationCapabilities presentationCapabilities,
        Func<ITerminalDriver?> driverFactory,
        Func<TerminalDiagnosticsSpokenOutputSection> probeSpokenOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSettingsPath);
        ArgumentNullException.ThrowIfNull(probeApiHealthAsync);
        ArgumentNullException.ThrowIfNull(readLocalStateAsync);
        ArgumentNullException.ThrowIfNull(presentationCapabilities);
        ArgumentNullException.ThrowIfNull(driverFactory);
        ArgumentNullException.ThrowIfNull(probeSpokenOutput);

        var client = new TerminalDiagnosticsClientSection(
            TerminalClientCommandLineText.ClientName,
            TerminalClientCommandLineText.InformationalVersion());

        var environment = new TerminalDiagnosticsEnvironmentSection(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            CultureInfo.CurrentCulture.Name);

        var options = configuration.Options;
        TerminalDiagnosticsSettingValue[] settings =
        [
            new("BaseUrl", options.BaseUri.ToString(), configuration.Origins["BaseUrl"]),
            new("Provider", options.Provider, configuration.Origins["Provider"]),
            new("Scenario", options.Scenario, configuration.Origins["Scenario"]),
            new(
                "RequestTimeout",
                options.RequestTimeout.ToString("c", CultureInfo.InvariantCulture),
                configuration.Origins["RequestTimeout"]),
        ];
        var configurationSection = new TerminalDiagnosticsConfigurationSection(
            appSettingsPath,
            appSettingsFileExists,
            settings);

        var apiProbe = await probeApiHealthAsync(cancellationToken);
        var apiSection = new TerminalDiagnosticsApiSection(
            options.BaseUri,
            options.BaseUri.IsLoopback,
            apiProbe.Health,
            apiProbe.StatusCode);

        var localState = await readLocalStateAsync(cancellationToken);

        var presentationDecision = TerminalPresentationSelector.Select(
            options,
            presentationCapabilities,
            driverFactory);
        try
        {
            var presentationSection = new TerminalDiagnosticsPresentationSection(
                presentationDecision.UsesTui,
                presentationDecision.Reason);

            var spokenOutputSection = probeSpokenOutput();

            return new TerminalDiagnosticsReport(
                client,
                environment,
                configurationSection,
                apiSection,
                localState,
                "not persisted; kept in memory only for this run",
                presentationSection,
                spokenOutputSection);
        }
        finally
        {
            // The selector may have initialized a real TUI driver (hiding the cursor, for
            // example) to measure the terminal; diagnostics never actually enters the TUI,
            // so it must restore it itself. A Plain decision never carries a driver.
            presentationDecision.Driver?.Restore();
        }
    }
}
