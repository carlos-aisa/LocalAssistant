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
    public void HelpDescribesEveryOptionAndTheEnvironmentPrefix()
    {
        var help = TerminalClientCommandLineText.Help();

        Assert.Contains("--base-url=", help, StringComparison.Ordinal);
        Assert.Contains("--provider=", help, StringComparison.Ordinal);
        Assert.Contains("--scenario=", help, StringComparison.Ordinal);
        Assert.Contains("--request-timeout=", help, StringComparison.Ordinal);
        Assert.Contains("--plain", help, StringComparison.Ordinal);
        Assert.Contains("--version", help, StringComparison.Ordinal);
        Assert.Contains("--help", help, StringComparison.Ordinal);
        Assert.Contains("LocalAssistant__TerminalClient__", help, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", help, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpDoesNotMentionADiagnosticsFlagThatDoesNotExistYet()
    {
        var help = TerminalClientCommandLineText.Help();

        Assert.DoesNotContain("--diagnostics", help, StringComparison.Ordinal);
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
}
