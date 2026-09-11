namespace LocalAssistant.TerminalClient;

/// <summary>
/// The raw command-line overrides for the terminal client. Values are left unvalidated
/// and unnormalized here; <see cref="TerminalClientConfiguration"/> combines them with
/// the other configuration sources and validates the result once.
/// </summary>
internal sealed record TerminalClientCommandLine(
    string? BaseUrl,
    string? Provider,
    string? Scenario,
    string? RequestTimeout,
    bool ForcePlain)
{
    public static TerminalClientCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? baseUrl = null;
        string? provider = null;
        string? scenario = null;
        string? requestTimeout = null;
        var forcePlain = false;

        foreach (var argument in args)
        {
            if (TryReadOption(argument, "--base-url=", out var value))
            {
                baseUrl = value;
                continue;
            }

            if (TryReadOption(argument, "--provider=", out value))
            {
                provider = value;
                continue;
            }

            if (TryReadOption(argument, "--scenario=", out value))
            {
                scenario = value;
                continue;
            }

            if (TryReadOption(argument, "--request-timeout=", out value))
            {
                requestTimeout = value;
                continue;
            }

            if (argument.Equals("--plain", StringComparison.Ordinal))
            {
                forcePlain = true;
                continue;
            }

            throw new ArgumentException("An unsupported command-line argument was supplied.");
        }

        return new TerminalClientCommandLine(baseUrl, provider, scenario, requestTimeout, forcePlain);
    }

    /// <summary>
    /// Whether <paramref name="args"/> requests the help text. Checked independently of
    /// <see cref="Parse"/> so it takes effect even alongside an otherwise invalid or
    /// unrecognized argument, and before any configuration source is read.
    /// </summary>
    public static bool RequestsHelp(string[] args) => Contains(args, "--help");

    /// <summary>
    /// Whether <paramref name="args"/> requests the version text. Same independence from
    /// <see cref="Parse"/> as <see cref="RequestsHelp"/>.
    /// </summary>
    public static bool RequestsVersion(string[] args) => Contains(args, "--version");

    private static bool Contains(string[] args, string flag)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Array.Exists(args, argument => argument.Equals(flag, StringComparison.Ordinal));
    }

    private static bool TryReadOption(string argument, string prefix, out string? value)
    {
        if (argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = argument[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }
}
