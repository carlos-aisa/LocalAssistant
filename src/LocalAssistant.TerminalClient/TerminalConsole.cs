using System.Text;

namespace LocalAssistant.TerminalClient;

public interface ITerminalConsole
{
    string? ReadLine();

    string ReadSecret();

    void Write(string value);

    void WriteLine(string value);
}

public sealed class SystemTerminalConsole : ITerminalConsole
{
    public string? ReadLine() => Console.ReadLine();

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

    public void Write(string value) => Console.Write(value);

    public void WriteLine(string value) => Console.WriteLine(value);
}
