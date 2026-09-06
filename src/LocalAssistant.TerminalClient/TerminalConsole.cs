using System.Text;

namespace LocalAssistant.TerminalClient;

internal enum TerminalInputKind
{
    Line,
    Secret,
}

internal sealed record TerminalInputRequest(TerminalInputKind Kind, string Prompt);

public interface ITerminalConsole
{
    string? ReadLine();

    string ReadSecret();

    void Write(string value);

    void WriteLine(string value);
}

internal interface IStructuredTerminalConsole : ITerminalConsole
{
    string? ReadLine(TerminalInputRequest request);

    string ReadSecret(TerminalInputRequest request);

    void WriteConversationMessage(string role, string content);

    void WriteError(ClientError clientError);
}

public sealed class SystemTerminalConsole : IStructuredTerminalConsole
{
    public string? ReadLine() => Console.ReadLine();

    string? IStructuredTerminalConsole.ReadLine(TerminalInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Console.Write(request.Prompt);
        return Console.ReadLine();
    }

    public string ReadSecret()
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "A private-client credential must be entered from an interactive console.");
        }

        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }

    string IStructuredTerminalConsole.ReadSecret(TerminalInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Console.Write(request.Prompt);
        return ReadSecret();
    }

    public void Write(string value) => Console.Write(value);

    public void WriteLine(string value) => Console.WriteLine(value);

    void IStructuredTerminalConsole.WriteConversationMessage(string role, string content) =>
        WriteLine($"{role}: {content}");

    void IStructuredTerminalConsole.WriteError(ClientError clientError)
    {
        ArgumentNullException.ThrowIfNull(clientError);
        var suffix = clientError.IsUncertain
            ? " The server may have received the operation; it was not retried."
            : string.Empty;
        WriteLine($"Error ({clientError.Code}): {clientError.Message}{suffix}");
    }
}
