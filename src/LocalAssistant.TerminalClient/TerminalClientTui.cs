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

    public IEnumerable<TerminalClientStateSnapshot> Drain()
    {
        while (_prioritySnapshots.TryDequeue(out var snapshot))
        {
            yield return snapshot;
        }

        var latest = Interlocked.Exchange(ref _latestSnapshot, null);
        if (latest is not null)
        {
            yield return latest;
        }
    }
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
            _transcript.Enqueue(value);
        }
    }
}

internal sealed class TerminalClientTuiHost
{
    private readonly TerminalClientTuiConsoleAdapter _console;
    private readonly TerminalClientTuiStateSink _stateSink;
    private readonly List<string> _transcript = [];
    private readonly StringBuilder _input = new();
    private TerminalClientStateSnapshot _snapshot = TerminalClientStateSnapshot.Initial;
    private bool _dirty = true;

    public TerminalClientTuiHost(
        TerminalClientTuiConsoleAdapter console,
        TerminalClientTuiStateSink stateSink)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _stateSink = stateSink ?? throw new ArgumentNullException(nameof(stateSink));
    }

    public async Task<int> RunAsync(TerminalClientApplication application, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var applicationTask = Task.Run(() => application.RunAsync(cancellationToken), CancellationToken.None);
        try
        {
            while (!applicationTask.IsCompleted)
            {
                DrainUpdates();
                ProcessAvailableInput();
                if (_dirty)
                {
                    Render();
                }

                await Task.Delay(30, CancellationToken.None);
            }

            DrainUpdates();
            Render();
            return await applicationTask;
        }
        finally
        {
            _console.CancelInput();
            TryRestoreTerminal();
        }
    }

    private void DrainUpdates()
    {
        foreach (var snapshot in _stateSink.Drain())
        {
            _snapshot = snapshot;
            _dirty = true;
        }

        foreach (var entry in _console.DrainTranscript())
        {
            _transcript.Add(entry);
            if (_transcript.Count > 100)
            {
                _transcript.RemoveAt(0);
            }

            _dirty = true;
        }
    }

    private void ProcessAvailableInput()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                ProcessKey(Console.ReadKey(intercept: true));
            }
        }
        catch (IOException)
        {
            _console.CancelInput();
        }
        catch (InvalidOperationException)
        {
            _console.CancelInput();
        }
    }

    private void ProcessKey(ConsoleKeyInfo key)
    {
        if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.D)
        {
            _input.Clear();
            _console.CompleteInput(null);
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

    private void Render()
    {
        _dirty = false;
        try
        {
            Console.Clear();
            Console.WriteLine("LocalAssistant terminal client");
            Console.WriteLine($"State: {_snapshot.Lifecycle} / {_snapshot.Activity}");
            Console.WriteLine($"Provider: {_snapshot.Provider ?? "unavailable"}");
            Console.WriteLine($"Conversation: {_snapshot.ConversationId?.ToString("N")[..8] ?? "none"}");

            if (_snapshot.Error is not null)
            {
                Console.WriteLine($"Error: {_snapshot.Error.SafeMessage} ({_snapshot.Error.Code})");
                if (_snapshot.Error.IsUncertain)
                {
                    Console.WriteLine("Result uncertain: the server may have received the operation.");
                }
            }

            if (_snapshot.PendingConfirmation is not null)
            {
                Console.WriteLine("CONFIRMATION REQUIRED");
                Console.WriteLine($"Tool: {_snapshot.PendingConfirmation.ToolName}");
                Console.WriteLine("Type approve or reject in the input line.");
            }

            Console.WriteLine();
            foreach (var entry in _transcript)
            {
                Console.WriteLine(entry);
            }

            if (_console.TryGetInputRequest(out var request) && request is not null)
            {
                var value = request.Kind == TerminalInputKind.Secret
                    ? new string('*', _input.Length)
                    : _input.ToString();
                Console.WriteLine();
                Console.Write($"{request.Prompt}{value}");
            }
        }
        catch (IOException)
        {
            _console.CancelInput();
        }
    }

    private static void TryRestoreTerminal()
    {
        try
        {
            Console.CursorVisible = true;
            Console.WriteLine();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
