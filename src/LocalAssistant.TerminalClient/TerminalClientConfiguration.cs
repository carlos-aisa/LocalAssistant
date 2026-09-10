using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace LocalAssistant.TerminalClient;

internal enum TerminalClientSettingOrigin
{
    Default,
    AppSettings,
    EnvironmentVariable,
    CommandLine,
}

internal sealed record TerminalClientConfigurationResult(
    TerminalClientOptions Options,
    IReadOnlyDictionary<string, TerminalClientSettingOrigin> Origins);

/// <summary>
/// Resolves the terminal client settings from, in increasing precedence, built-in
/// defaults, <c>appsettings.json</c> next to the executable, environment variables
/// prefixed with <see cref="EnvironmentPrefix"/>, and command-line arguments. The
/// combined result is validated once and the origin of every setting is retained so the
/// diagnostics mode can report it.
/// </summary>
internal static class TerminalClientConfiguration
{
    internal const string SectionName = "TerminalClient";
    internal const string EnvironmentPrefix = "LocalAssistant__";
    internal const string AppSettingsFileName = "appsettings.json";

    private const string DefaultBaseUrl = "http://localhost:5100";
    private const string DefaultProvider = "ollama";
    private const string DefaultScenario = "direct";

    private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromHours(1);

    public static TerminalClientConfigurationResult Load(string[] args) =>
        Load(args, AppContext.BaseDirectory);

    public static TerminalClientConfigurationResult Load(string[] args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(baseDirectory);

        return Load(args, BuildFileConfiguration(baseDirectory), BuildEnvironmentConfiguration());
    }

    public static TerminalClientConfigurationResult LoadFromCommandLine(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var empty = new ConfigurationBuilder().Build();
        return Load(args, empty, empty);
    }

    internal static TerminalClientConfigurationResult Load(
        string[] args,
        IConfiguration fileConfiguration,
        IConfiguration environmentConfiguration)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(fileConfiguration);
        ArgumentNullException.ThrowIfNull(environmentConfiguration);

        var commandLine = TerminalClientCommandLine.Parse(args);
        var file = fileConfiguration.GetSection(SectionName);
        var environment = environmentConfiguration.GetSection(SectionName);

        var baseUrl = Resolve("BaseUrl", DefaultBaseUrl, file, environment, commandLine.BaseUrl);
        var provider = Resolve("Provider", DefaultProvider, file, environment, commandLine.Provider);
        var scenario = Resolve("Scenario", DefaultScenario, file, environment, commandLine.Scenario);
        var requestTimeout = Resolve(
            "RequestTimeout",
            FormatTimeout(TerminalClientOptions.DefaultRequestTimeout),
            file,
            environment,
            commandLine.RequestTimeout);

        var options = Build(baseUrl, provider, scenario, requestTimeout, commandLine.ForcePlain);
        var origins = new Dictionary<string, TerminalClientSettingOrigin>(StringComparer.Ordinal)
        {
            ["BaseUrl"] = baseUrl.Origin,
            ["Provider"] = provider.Origin,
            ["Scenario"] = scenario.Origin,
            ["RequestTimeout"] = requestTimeout.Origin,
        };

        return new TerminalClientConfigurationResult(options, origins);
    }

    private static TerminalClientSetting Resolve(
        string key,
        string defaultValue,
        IConfiguration file,
        IConfiguration environment,
        string? commandLineValue)
    {
        if (commandLineValue is not null)
        {
            return new TerminalClientSetting(commandLineValue, TerminalClientSettingOrigin.CommandLine);
        }

        var environmentValue = environment[key];
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return new TerminalClientSetting(environmentValue, TerminalClientSettingOrigin.EnvironmentVariable);
        }

        var fileValue = file[key];
        if (!string.IsNullOrWhiteSpace(fileValue))
        {
            return new TerminalClientSetting(fileValue, TerminalClientSettingOrigin.AppSettings);
        }

        return new TerminalClientSetting(defaultValue, TerminalClientSettingOrigin.Default);
    }

    private static TerminalClientOptions Build(
        TerminalClientSetting baseUrl,
        TerminalClientSetting provider,
        TerminalClientSetting scenario,
        TerminalClientSetting requestTimeout,
        bool forcePlain)
    {
        if (!Uri.TryCreate(baseUrl.Value, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !baseUri.IsLoopback)
        {
            throw InvalidSetting(
                "Base URL must use HTTP or HTTPS and target a loopback host.",
                "BaseUrl",
                baseUrl.Origin);
        }

        var normalizedProvider = provider.Value.Trim().ToLowerInvariant();
        if (normalizedProvider is not ("fake" or "ollama"))
        {
            throw InvalidSetting("Provider must be 'fake' or 'ollama'.", "Provider", provider.Origin);
        }

        if (string.IsNullOrWhiteSpace(scenario.Value))
        {
            throw InvalidSetting("Scenario must not be empty.", "Scenario", scenario.Origin);
        }

        if (!TimeSpan.TryParse(requestTimeout.Value, CultureInfo.InvariantCulture, out var timeout) ||
            timeout <= TimeSpan.Zero ||
            timeout > MaximumRequestTimeout)
        {
            throw InvalidSetting(
                "Request timeout must be a positive duration no greater than '01:00:00', such as '00:04:00'.",
                "RequestTimeout",
                requestTimeout.Origin);
        }

        return new TerminalClientOptions(
            new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute),
            normalizedProvider,
            scenario.Value.Trim(),
            timeout,
            forcePlain);
    }

    private static ArgumentException InvalidSetting(
        string message,
        string key,
        TerminalClientSettingOrigin origin) =>
        new(origin switch
        {
            TerminalClientSettingOrigin.AppSettings =>
                $"{message} (source: {AppSettingsFileName}, {SectionName}:{key})",
            TerminalClientSettingOrigin.EnvironmentVariable =>
                $"{message} (source: environment variable {EnvironmentPrefix}{SectionName}__{key})",
            _ => message,
        });

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.ToString("c", CultureInfo.InvariantCulture);

    private static IConfiguration BuildFileConfiguration(string baseDirectory)
    {
        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(
                    Path.Combine(baseDirectory, AppSettingsFileName),
                    optional: true,
                    reloadOnChange: false)
                .Build();
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            throw new ArgumentException(
                $"The terminal client configuration file {AppSettingsFileName} is not valid JSON.");
        }
    }

    private static IConfiguration BuildEnvironmentConfiguration() =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvironmentPrefix)
            .Build();

    private readonly record struct TerminalClientSetting(string Value, TerminalClientSettingOrigin Origin);
}
