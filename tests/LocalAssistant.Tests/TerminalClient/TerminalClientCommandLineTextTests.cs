using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientCommandLineTextTests
{
    [Fact]
    public void VersionStartsWithTheClientNameAndIsNotBlank()
    {
        var version = TerminalClientCommandLineText.Version();

        Assert.StartsWith("LocalAssistant.TerminalClient", version, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void VersionCombinesTheClientNameAndTheInformationalVersion()
    {
        Assert.Equal(
            $"{TerminalClientCommandLineText.ClientName} {TerminalClientCommandLineText.InformationalVersion()}",
            TerminalClientCommandLineText.Version());
    }

    [Fact]
    public void InformationalVersionIsNeverBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(TerminalClientCommandLineText.InformationalVersion()));
    }

    [Fact]
    public void HelpDescribesEveryOptionAndTheEnvironmentPrefix()
    {
        var help = TerminalClientCommandLineText.Help();

        Assert.Contains("--base-url=", help, StringComparison.Ordinal);
        Assert.Contains("--provider=", help, StringComparison.Ordinal);
        Assert.Contains("--scenario=", help, StringComparison.Ordinal);
        Assert.Contains("--request-timeout=", help, StringComparison.Ordinal);
        Assert.Contains("--plain", help, StringComparison.Ordinal);
        Assert.Contains("--diagnostics", help, StringComparison.Ordinal);
        Assert.Contains("--version", help, StringComparison.Ordinal);
        Assert.Contains("--help", help, StringComparison.Ordinal);
        Assert.Contains("LocalAssistant__TerminalClient__", help, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", help, StringComparison.Ordinal);
    }

    public static TheoryData<string[], bool> HelpDetectionCases => new()
    {
        { ["--help"], true },
        { ["--provider=fake", "--help"], true },
        { [], false },
        { ["--version"], false },
    };

    [Theory]
    [MemberData(nameof(HelpDetectionCases))]
    public void RequestsHelpDetectsTheFlagAnywhereInTheArguments(string[] args, bool expected)
    {
        Assert.Equal(expected, TerminalClientCommandLine.RequestsHelp(args));
    }

    public static TheoryData<string[], bool> VersionDetectionCases => new()
    {
        { ["--version"], true },
        { ["--plain", "--version"], true },
        { [], false },
        { ["--help"], false },
    };

    [Theory]
    [MemberData(nameof(VersionDetectionCases))]
    public void RequestsVersionDetectsTheFlagAnywhereInTheArguments(string[] args, bool expected)
    {
        Assert.Equal(expected, TerminalClientCommandLine.RequestsVersion(args));
    }

    public static TheoryData<string[], bool> DiagnosticsDetectionCases => new()
    {
        { ["--diagnostics"], true },
        { ["--provider=fake", "--diagnostics"], true },
        { [], false },
        { ["--help"], false },
    };

    [Theory]
    [MemberData(nameof(DiagnosticsDetectionCases))]
    public void RequestsDiagnosticsDetectsTheFlagAnywhereInTheArguments(string[] args, bool expected)
    {
        Assert.Equal(expected, TerminalClientCommandLine.RequestsDiagnostics(args));
    }

    [Fact]
    public void DiagnosticsIsRecognizedByParseAndDoesNotChangeAnySetting()
    {
        var commandLine = TerminalClientCommandLine.Parse(["--diagnostics", "--provider=fake"]);

        Assert.Null(commandLine.BaseUrl);
        Assert.Equal("fake", commandLine.Provider);
        Assert.Null(commandLine.Scenario);
        Assert.Null(commandLine.RequestTimeout);
        Assert.False(commandLine.ForcePlain);
    }
}
