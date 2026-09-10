using LocalAssistant.TerminalClient;
using Microsoft.Extensions.Configuration;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientConfigurationTests
{
    [Fact]
    public void UsesBuiltInDefaultsWhenNoSourceProvidesAValue()
    {
        var result = TerminalClientConfiguration.Load([], Empty(), Empty());

        Assert.Equal("http://localhost:5100/", result.Options.BaseUri.ToString());
        Assert.Equal("ollama", result.Options.Provider);
        Assert.Equal("direct", result.Options.Scenario);
        Assert.Equal(TimeSpan.FromMinutes(4), result.Options.RequestTimeout);
        Assert.False(result.Options.ForcePlain);
        Assert.All(result.Origins.Values, origin => Assert.Equal(TerminalClientSettingOrigin.Default, origin));
    }

    [Fact]
    public void AppliesPrecedenceCommandLineOverEnvironmentOverFileOverDefault()
    {
        var file = Configuration(
            ("TerminalClient:BaseUrl", "http://localhost:6001"),
            ("TerminalClient:Provider", "fake"),
            ("TerminalClient:Scenario", "time"),
            ("TerminalClient:RequestTimeout", "00:01:00"));
        var environment = Configuration(
            ("TerminalClient:Provider", "ollama"),
            ("TerminalClient:Scenario", "temperature"));

        var result = TerminalClientConfiguration.Load(["--scenario=direct"], file, environment);

        Assert.Equal("http://localhost:6001/", result.Options.BaseUri.ToString());
        Assert.Equal("ollama", result.Options.Provider);
        Assert.Equal("direct", result.Options.Scenario);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Options.RequestTimeout);

        Assert.Equal(TerminalClientSettingOrigin.AppSettings, result.Origins["BaseUrl"]);
        Assert.Equal(TerminalClientSettingOrigin.EnvironmentVariable, result.Origins["Provider"]);
        Assert.Equal(TerminalClientSettingOrigin.CommandLine, result.Origins["Scenario"]);
        Assert.Equal(TerminalClientSettingOrigin.AppSettings, result.Origins["RequestTimeout"]);
    }

    [Fact]
    public void EnvironmentOverridesFileForASingleKey()
    {
        var file = Configuration(("TerminalClient:BaseUrl", "http://localhost:6001"));
        var environment = Configuration(("TerminalClient:BaseUrl", "http://localhost:7002"));

        var result = TerminalClientConfiguration.Load([], file, environment);

        Assert.Equal("http://localhost:7002/", result.Options.BaseUri.ToString());
        Assert.Equal(TerminalClientSettingOrigin.EnvironmentVariable, result.Origins["BaseUrl"]);
    }

    [Fact]
    public void BlankValuesFallThroughToTheNextSource()
    {
        var environment = Configuration(("TerminalClient:Scenario", "   "));

        var result = TerminalClientConfiguration.Load([], Empty(), environment);

        Assert.Equal("direct", result.Options.Scenario);
        Assert.Equal(TerminalClientSettingOrigin.Default, result.Origins["Scenario"]);
    }

    [Fact]
    public void NormalizesProviderComingFromTheEnvironment()
    {
        var environment = Configuration(("TerminalClient:Provider", "  OLLAMA "));

        var result = TerminalClientConfiguration.Load([], Empty(), environment);

        Assert.Equal("ollama", result.Options.Provider);
        Assert.Equal(TerminalClientSettingOrigin.EnvironmentVariable, result.Origins["Provider"]);
    }

    [Fact]
    public void MissingAppSettingsFileIsNotAnError()
    {
        using var directory = new TemporaryDirectory();

        var result = TerminalClientConfiguration.Load([], directory.Path);

        Assert.Equal("http://localhost:5100/", result.Options.BaseUri.ToString());
        Assert.Equal(TerminalClientSettingOrigin.Default, result.Origins["BaseUrl"]);
    }

    [Fact]
    public void ReadsValuesFromAnAppSettingsFileOnDisk()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "appsettings.json"),
            """{ "TerminalClient": { "Provider": "fake", "RequestTimeout": "00:00:30" } }""");

        var result = TerminalClientConfiguration.Load([], directory.Path);

        Assert.Equal("fake", result.Options.Provider);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Options.RequestTimeout);
        Assert.Equal(TerminalClientSettingOrigin.AppSettings, result.Origins["Provider"]);
        Assert.Equal(TerminalClientSettingOrigin.AppSettings, result.Origins["RequestTimeout"]);
    }

    [Fact]
    public void MalformedAppSettingsFileIsRejectedWithAClearMessage()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "appsettings.json"), "{ not valid json ");

        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.Load([], directory.Path));

        Assert.Contains("appsettings.json", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    [InlineData("half an hour")]
    [InlineData("2.00:00:00")]
    public void RejectsNonPositiveOrUnparsableRequestTimeoutAndNamesTheSource(string value)
    {
        var environment = Configuration(("TerminalClient:RequestTimeout", value));

        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.Load([], Empty(), environment));

        Assert.Contains("Request timeout", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "environment variable LocalAssistant__TerminalClient__RequestTimeout",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonLoopbackBaseUrlFromTheEnvironmentAndNamesTheSource()
    {
        var environment = Configuration(("TerminalClient:BaseUrl", "http://example.com"));

        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.Load([], Empty(), environment));

        Assert.Contains("loopback", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "environment variable LocalAssistant__TerminalClient__BaseUrl",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInvalidSchemeFromTheFileAndNamesTheSource()
    {
        var file = Configuration(("TerminalClient:BaseUrl", "ftp://localhost"));

        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.Load([], file, Empty()));

        Assert.Contains("appsettings.json, TerminalClient:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownProviderFromTheFileAndNamesTheSource()
    {
        var file = Configuration(("TerminalClient:Provider", "azure"));

        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.Load([], file, Empty()));

        Assert.Contains("Provider must be 'fake' or 'ollama'.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("appsettings.json, TerminalClient:Provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineValidationFailuresKeepThePlainMessageWithoutASource()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.LoadFromCommandLine(["--provider=azure"]));

        Assert.Equal("Provider must be 'fake' or 'ollama'.", exception.Message);
    }

    [Fact]
    public void UnknownCommandLineArgumentIsStillRejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientConfiguration.LoadFromCommandLine(["--unknown"]));

        Assert.Contains("unsupported command-line argument", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePreservesExistingCommandLineBehaviour()
    {
        var options = TerminalClientOptions.Parse(
            ["--provider=fake", "--scenario=direct", "--base-url=http://localhost:5200"]);

        Assert.Equal("fake", options.Provider);
        Assert.Equal("direct", options.Scenario);
        Assert.Equal("http://localhost:5200/", options.BaseUri.ToString());
        Assert.Equal(TerminalClientOptions.DefaultRequestTimeout, options.RequestTimeout);
        Assert.False(options.ForcePlain);
    }

    [Fact]
    public void ParseAcceptsPlainAndRequestTimeoutArguments()
    {
        var options = TerminalClientOptions.Parse(["--plain", "--request-timeout=00:02:30"]);

        Assert.True(options.ForcePlain);
        Assert.Equal(TimeSpan.FromSeconds(150), options.RequestTimeout);
    }

    [Fact]
    public void AcceptsARequestTimeoutUpToOneHour()
    {
        var result = TerminalClientConfiguration.Load(
            ["--request-timeout=01:00:00", "--provider=fake"],
            Empty(),
            Empty());

        Assert.Equal(TimeSpan.FromHours(1), result.Options.RequestTimeout);
    }

    [Fact]
    public void EmptyScenarioArgumentIsRejectedWithTheExistingMessage()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => TerminalClientOptions.Parse(["--scenario="]));

        Assert.Equal("Scenario must not be empty.", exception.Message);
    }

    private static IConfiguration Empty() => new ConfigurationBuilder().Build();

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(
                entry => entry.Key,
                entry => (string?)entry.Value))
            .Build();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localassistant-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
