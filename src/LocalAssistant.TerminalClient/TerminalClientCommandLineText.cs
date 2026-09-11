using System.Globalization;
using System.Reflection;

namespace LocalAssistant.TerminalClient;

/// <summary>
/// Renders the <c>--version</c> and <c>--help</c> text. Kept apart from
/// <see cref="TerminalClientCommandLine"/> so parsing stays free of presentation
/// concerns; both texts are pure and depend only on the assembly metadata and the
/// configuration defaults they describe.
/// </summary>
internal static class TerminalClientCommandLineText
{
    public static string Version()
    {
        var informationalVersion = typeof(TerminalClientCommandLineText).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "LocalAssistant.TerminalClient"
            : $"LocalAssistant.TerminalClient {informationalVersion}";
    }

    public static string Help()
    {
        string[] lines =
        [
            "LocalAssistant.TerminalClient",
            string.Empty,
            "Usage: LocalAssistant.TerminalClient [options]",
            string.Empty,
            "Options:",
            $"  --base-url=<url>              HTTP(S) loopback base URL of the API " +
                $"(default: {TerminalClientConfiguration.DefaultBaseUrl}).",
            $"  --provider=<fake|ollama>      Language provider to use " +
                $"(default: {TerminalClientConfiguration.DefaultProvider}).",
            $"  --scenario=<name>             Fake-provider scenario name " +
                $"(default: {TerminalClientConfiguration.DefaultScenario}).",
            $"  --request-timeout=<hh:mm:ss>  HTTP request timeout, greater than zero and up " +
                $"to {FormatTimeout(TerminalClientConfiguration.MaximumRequestTimeout)} " +
                $"(default: {FormatTimeout(TerminalClientOptions.DefaultRequestTimeout)}).",
            "  --plain                       Force the plain text presentation instead of the TUI.",
            "  --version                     Print the client version and exit.",
            "  --help                        Print this help text and exit.",
            string.Empty,
            "Configuration can also come from an appsettings.json file next to the executable",
            $"(section \"{TerminalClientConfiguration.SectionName}\") or from environment " +
                "variables prefixed with",
            $"{TerminalClientConfiguration.EnvironmentPrefix}{TerminalClientConfiguration.SectionName}__ " +
                "(for example",
            $"{TerminalClientConfiguration.EnvironmentPrefix}{TerminalClientConfiguration.SectionName}__BaseUrl).",
            "Command-line arguments take precedence over environment variables, which take",
            "precedence over the file, which takes precedence over these built-in defaults.",
        ];

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatTimeout(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);
}
