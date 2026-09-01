namespace LocalAssistant.TerminalClient;

public sealed record TerminalClientOptions(Uri BaseUri, string Provider, string Scenario)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(4);

    public static TerminalClientOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var baseUrl = "http://localhost:5100";
        var provider = "ollama";
        var scenario = "direct";

        foreach (var argument in args)
        {
            if (argument.StartsWith("--base-url=", StringComparison.Ordinal))
            {
                baseUrl = argument["--base-url=".Length..];
                continue;
            }

            if (argument.StartsWith("--provider=", StringComparison.Ordinal))
            {
                provider = argument["--provider=".Length..];
                continue;
            }

            if (argument.StartsWith("--scenario=", StringComparison.Ordinal))
            {
                scenario = argument["--scenario=".Length..];
                continue;
            }

            throw new ArgumentException("An unsupported command-line argument was supplied.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !baseUri.IsLoopback)
        {
            throw new ArgumentException(
                "Base URL must use HTTP or HTTPS and target a loopback host.");
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        if (normalizedProvider is not ("fake" or "ollama"))
        {
            throw new ArgumentException("Provider must be 'fake' or 'ollama'.");
        }

        if (string.IsNullOrWhiteSpace(scenario))
        {
            throw new ArgumentException("Scenario must not be empty.");
        }

        return new(
            new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute),
            normalizedProvider,
            scenario.Trim());
    }
}
