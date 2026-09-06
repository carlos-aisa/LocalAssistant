using System.Collections.Concurrent;
using System.Text;

namespace LocalAssistant.TerminalClient;

internal sealed class TerminalClientTuiStateSink : ITerminalClientStateSink
{
    private readonly ConcurrentQueue<TerminalClientStateSnapshot> _prioritySnapshots = new();
    private TerminalClientStateSnapshot? _latestSnapshot;

    public void OnStateChanged(TerminalClientStateSnapshot snapshot)
    {
        var previous = Interlocked.Exchange(ref _latestSnapshot, snapshot);
        if (snapshot.PendingConfirmation != previous?.PendingConfirmation || snapshot.Error != previous?.Error)
        {
            _prioritySnapshots.Enqueue(snapshot);
        }
    }

    public IEnumerable<TerminalClientStateSnapshot> DrainPriority()
    {
        while (_prioritySnapshots.TryDequeue(out var snapshot))
        {
            yield return snapshot;
        }
    }

    public TerminalClientStateSnapshot? TakeLatest() =>
        Interlocked.Exchange(ref _latestSnapshot, null);
}

internal sealed class TerminalClientTuiConsoleAdapter : IStructuredTerminalConsole
{
    private readonly ConcurrentQueue<string> _transcript = new();
    private readonly object _inputLock = new();
    private TaskCompletionSource<bool> _inputPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string?>? _inputCompletion;
    private TerminalInputRequest? _inputRequest;

    public string? ReadLine() => RequestInput(new TerminalInputRequest(TerminalInputKind.Line, string.Empty));

    public string ReadSecret() => RequestInput(new TerminalInputRequest(TerminalInputKind.Secret, string.Empty)) ?? string.Empty;

    public string? ReadLine(TerminalInputRequest request) => RequestInput(request);

    public string ReadSecret(TerminalInputRequest request) => RequestInput(request) ?? string.Empty;

    public void Write(string value) => AddTranscript(value);

    public void WriteLine(string value) => AddTranscript(value);

    public void WriteConversationMessage(string role, string content) => AddTranscript($"{role}: {content}");

    public void WriteError(ClientError error)
    {
        var suffix = error.IsUncertain ? " Result uncertain." : string.Empty;
        AddTranscript($"Error ({error.Code}): {error.Message}{suffix}");
    }

    public bool TryGetInputRequest(out TerminalInputRequest? request)
    {
        lock (_inputLock)
        {
            request = _inputRequest;
            return request is not null;
        }
    }

    internal Task WaitForInputAsync()
    {
        lock (_inputLock)
        {
            return _inputRequest is null ? _inputPublished.Task : Task.CompletedTask;
        }
    }

    public void CompleteInput(string? value)
    {
        TaskCompletionSource<string?>? completion;
        lock (_inputLock)
        {
            completion = _inputCompletion;
            _inputCompletion = null;
            _inputRequest = null;
            _inputPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        completion?.TrySetResult(value);
    }

    public void CancelInput() => CompleteInput(null);

    public IReadOnlyList<string> DrainTranscript()
    {
        var entries = new List<string>();
        while (_transcript.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private string? RequestInput(TerminalInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_inputLock)
        {
            _inputRequest = request;
            _inputCompletion = completion;
            _inputPublished.TrySetResult(true);
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private void AddTranscript(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _transcript.Enqueue(TerminalTextSanitizer.Normalize(value));
        }
    }
}

internal sealed class TerminalClientTuiHost
{
    private const int MaximumTranscriptEntries = 100;
    private readonly TerminalClientTuiConsoleAdapter _console;
    private readonly TerminalClientTuiStateSink _stateSink;
    private readonly ITerminalDriver _driver;
    private readonly List<string> _transcript = [];
    private readonly StringBuilder _input = new();
    private TerminalClientStateSnapshot _snapshot = TerminalClientStateSnapshot.Initial;
    private TerminalSize? _lastSize;
    private int _scrollOffset;
    private bool _dirty = true;

    public TerminalClientTuiHost(
        TerminalClientTuiConsoleAdapter console,
        TerminalClientTuiStateSink stateSink,
        ITerminalDriver driver)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _stateSink = stateSink ?? throw new ArgumentNullException(nameof(stateSink));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public async Task<int> RunAsync(TerminalClientApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        return await RunAsync(application.RunAsync, cancellationToken);
    }

    internal async Task<int> RunAsync(
        Func<CancellationToken, Task<int>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var cancellationRegistration = cancellationToken.Register(_console.CancelInput);
        var applicationTask = Task.Run(() => operation(cancellationToken), CancellationToken.None);
        try
        {
            while (!applicationTask.IsCompleted)
            {
                DrainUpdates();
                ProcessAvailableInput();
                DetectResize();
                if (_dirty)
                {
                    Render();
                }

                await Task.Delay(30, CancellationToken.None);
            }

            DrainUpdates();
            DetectResize();
            if (_dirty)
            {
                Render();
            }

            return await applicationTask;
        }
        finally
        {
            _console.CancelInput();
            _driver.Restore();
        }
    }

    private void DrainUpdates()
    {
        foreach (var snapshot in _stateSink.DrainPriority())
        {
            _snapshot = snapshot;
            Render();
        }

        var latest = _stateSink.TakeLatest();
        if (latest is not null)
        {
            _snapshot = latest;
            _dirty = true;
        }

        foreach (var entry in _console.DrainTranscript())
        {
            _transcript.Add(entry);
            if (_transcript.Count > MaximumTranscriptEntries)
            {
                _transcript.RemoveAt(0);
            }

            _scrollOffset = 0;
            _dirty = true;
        }
    }

    private void ProcessAvailableInput()
    {
        while (true)
        {
            var input = _driver.TryReadInput();
            if (input is null)
            {
                return;
            }

            if (input.IsEndOfInput)
            {
                _input.Clear();
                _console.CancelInput();
                _dirty = true;
                return;
            }

            if (input.Key.HasValue)
            {
                ProcessKey(input.Key.Value);
            }
        }
    }

    private void ProcessKey(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.PageUp)
        {
            _scrollOffset += key.Key == ConsoleKey.PageUp ? 5 : 1;
            _dirty = true;
            return;
        }

        if (key.Key is ConsoleKey.DownArrow or ConsoleKey.PageDown)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - (key.Key == ConsoleKey.PageDown ? 5 : 1));
            _dirty = true;
            return;
        }

        if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.D)
        {
            _input.Clear();
            _console.CancelInput();
            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            _input.Clear();
            _console.CompleteInput(string.Empty);
            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var value = _input.ToString();
            _input.Clear();
            _console.CompleteInput(value);
            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (_input.Length > 0)
            {
                _input.Length--;
                _dirty = true;
            }

            return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _input.Append(key.KeyChar);
            _dirty = true;
        }
    }

    private void DetectResize()
    {
        var size = _driver.GetSize();
        if (_lastSize != size)
        {
            _lastSize = size;
            _dirty = true;
        }
    }

    private void Render()
    {
        _dirty = false;
        var size = _lastSize ?? _driver.GetSize();
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var statusLines = CreateStatusLines(width, height);
        var footerLines = CreateFooterLines(width, height);
        var transcriptHeight = Math.Max(0, height - statusLines.Count - footerLines.Count);
        var lines = new List<string>(height);

        lines.AddRange(statusLines);
        var transcriptLines = CreateTranscriptLines(width);
        var visibleTranscript = transcriptLines
            .Skip(Math.Max(0, transcriptLines.Count - transcriptHeight - _scrollOffset))
            .Take(transcriptHeight)
            .ToList();
        lines.AddRange(visibleTranscript);
        lines.AddRange(footerLines);

        _driver.Render(lines.Take(height).ToList());
    }

    private List<string> CreateStatusLines(int width, int height)
    {
        var lines = new List<string>();
        if (height <= 3)
        {
            lines.Add(
                $"State: {_snapshot.Lifecycle} / {_snapshot.Activity}; " +
                $"Provider: {_snapshot.Provider ?? "unavailable"}; " +
                $"Conversation: {_snapshot.ConversationId?.ToString("N")[..8] ?? "none"}");
        }
        else
        {
            if (height >= 10)
            {
                lines.Add("LocalAssistant terminal client");
            }

            lines.Add($"State: {_snapshot.Lifecycle} / {_snapshot.Activity}");
            lines.Add(
                $"Provider: {_snapshot.Provider ?? "unavailable"}; " +
                $"Conversation: {_snapshot.ConversationId?.ToString("N")[..8] ?? "none"}");
        }

        return lines.Select(line => Truncate(line, width)).ToList();
    }

    private List<string> CreateFooterLines(int width, int height)
    {
        var lines = new List<string>();

        if (_snapshot.Error is not null)
        {
            var uncertainty = _snapshot.Error.IsUncertain
                ? " Result uncertain: the server may have received the operation."
                : string.Empty;
            lines.Add($"Error: {_snapshot.Error.SafeMessage} ({_snapshot.Error.Code}).{uncertainty}");
        }

        if (_snapshot.PendingConfirmation is not null)
        {
            if (height < 7)
            {
                lines.Add(
                    $"CONFIRMATION REQUIRED: {_snapshot.PendingConfirmation.ToolName}; " +
                    $"expires {_snapshot.PendingConfirmation.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC");
            }
            else
            {
                lines.Add("CONFIRMATION REQUIRED");
                lines.Add($"Tool: {_snapshot.PendingConfirmation.ToolName}");
                lines.Add(
                    $"Expires: {_snapshot.PendingConfirmation.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC");
                lines.Add("Type approve or reject in the input line.");
            }
        }

        if (_console.TryGetInputRequest(out var request) && request is not null)
        {
            var value = request.Kind == TerminalInputKind.Secret
                ? new string('*', _input.Length)
                : TerminalTextSanitizer.Normalize(_input.ToString());
            lines.Add($"{TerminalTextSanitizer.Normalize(request.Prompt)}{value}");
        }

        return lines.Select(line => Truncate(line, width)).ToList();
    }

    private List<string> CreateTranscriptLines(int width)
    {
        var lines = new List<string>();
        foreach (var entry in _transcript)
        {
            foreach (var line in entry.Split('\n'))
            {
                if (line.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                for (var offset = 0; offset < line.Length; offset += width)
                {
                    lines.Add(line.Substring(offset, Math.Min(width, line.Length - offset)));
                }
            }
        }

        return lines;
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..width];
}
