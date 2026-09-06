using System.Collections.Concurrent;
using System.Globalization;
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
    private bool _inputClosed;

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

    public void CloseInput()
    {
        TaskCompletionSource<string?>? completion;
        lock (_inputLock)
        {
            if (_inputClosed)
            {
                return;
            }

            _inputClosed = true;
            completion = _inputCompletion;
            _inputCompletion = null;
            _inputRequest = null;
            _inputPublished.TrySetResult(true);
        }

        completion?.TrySetResult(null);
    }

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
            if (_inputClosed)
            {
                return null;
            }

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
    internal const int MinimumWidth = 40;
    internal const int MinimumHeight = 8;

    private readonly TerminalClientTuiConsoleAdapter _console;
    private readonly TerminalClientTuiStateSink _stateSink;
    private readonly ITerminalDriver _driver;
    private readonly TerminalClientTuiTranscript _transcript = new();
    private readonly StringBuilder _input = new();
    private TerminalClientStateSnapshot _snapshot = TerminalClientStateSnapshot.Initial;
    private TerminalSize? _lastSize;
    private int _scrollOffset;
    private int _clearInputRequested;
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
        using var cancellationRegistration = cancellationToken.Register(RequestTerminalClose);
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
            _console.CloseInput();
            ClearInputBuffer();
            _driver.Restore();
        }
    }

    private void RequestTerminalClose()
    {
        _console.CloseInput();
        Interlocked.Exchange(ref _clearInputRequested, 1);
        _dirty = true;
    }

    private void DrainUpdates()
    {
        if (Interlocked.Exchange(ref _clearInputRequested, 0) == 1)
        {
            ClearInputBuffer();
        }

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
                RequestTerminalClose();
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

        if (IsEndOfInputKey(key))
        {
            if (_input.Length == 0)
            {
                RequestTerminalClose();
            }

            return;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            ClearInputBuffer();
            _console.CompleteInput(string.Empty);
            _dirty = true;
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var value = _input.ToString();
            ClearInputBuffer();
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

    private static bool IsEndOfInputKey(ConsoleKeyInfo key) =>
        (key.Modifiers & ConsoleModifiers.Control) != 0 &&
        key.Key is ConsoleKey.D or ConsoleKey.Z;

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
        var lines = width < MinimumWidth || height < MinimumHeight
            ? CreateCompactFrame(width, height)
            : CreateFrame(width, height);

        _driver.Render(lines);
    }

    private List<string> CreateFrame(int width, int height)
    {
        var priorityLines = new List<string>();
        AddInputLine(priorityLines, width);
        AddConfirmationLine(priorityLines, width);
        AddErrorLine(priorityLines, width);
        priorityLines.Add(FitLine(CreateStateLine(), width));

        var transcriptHeight = Math.Max(0, height - priorityLines.Count);
        var transcript = _transcript.CreateLines(width);
        var first = Math.Max(0, transcript.Count - transcriptHeight - _scrollOffset);
        var visibleTranscript = transcript.Skip(first).Take(transcriptHeight);
        return visibleTranscript.Concat(priorityLines).Take(height).ToList();
    }

    private List<string> CreateCompactFrame(int width, int height)
    {
        var lines = new List<string>();
        AddInputLine(lines, width);
        AddConfirmationLine(lines, width);
        AddErrorLine(lines, width);
        lines.Add(FitLine("Terminal too small. Resize to at least 40x8.", width));
        return lines.Take(height).ToList();
    }

    private void AddInputLine(List<string> lines, int width)
    {
        if (!_console.TryGetInputRequest(out var request) || request is null)
        {
            return;
        }

        var prompt = TerminalTextSanitizer.NormalizeSingleLine(request.Prompt);
        var value = request.Kind == TerminalInputKind.Secret
            ? new string('*', _input.Length)
            : TerminalTextSanitizer.NormalizeSingleLine(_input.ToString());
        lines.Add(FitInputLine(prompt, value, width));
    }

    private void AddConfirmationLine(List<string> lines, int width)
    {
        if (_snapshot.PendingConfirmation is null)
        {
            return;
        }

        var expiry = _snapshot.PendingConfirmation.ExpiresAtUtc.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm 'UTC'",
            CultureInfo.InvariantCulture);
        var suffix = $"; expires {expiry}";
        var toolWidth = Math.Max(0, width - "CONFIRM: ".Length - suffix.Length);
        var tool = TerminalTextSanitizer.NormalizeSingleLine(_snapshot.PendingConfirmation.ToolName);
        lines.Add(FitLine($"CONFIRM: {FitLine(tool, toolWidth)}{suffix}", width));
    }

    private void AddErrorLine(List<string> lines, int width)
    {
        if (_snapshot.Error is null)
        {
            return;
        }

        var prefix = _snapshot.Error.IsUncertain ? "ERROR [UNCERTAIN]: " : "ERROR: ";
        var code = TerminalTextSanitizer.NormalizeSingleLine(_snapshot.Error.Code);
        var message = TerminalTextSanitizer.NormalizeSingleLine(_snapshot.Error.SafeMessage);
        lines.Add(FitLine($"{prefix}{code}: {message}", width));
    }

    private string CreateStateLine()
    {
        var provider = TerminalTextSanitizer.NormalizeSingleLine(_snapshot.Provider ?? "unavailable");
        var conversation = _snapshot.ConversationId?.ToString("N")[..8] ?? "none";
        return $"State: {_snapshot.Lifecycle}/{_snapshot.Activity}; Provider: {provider}; Conversation: {conversation}";
    }

    private static string FitInputLine(string prompt, string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (prompt.Length >= width)
        {
            return FitTail(value, width);
        }

        return FitTail(prompt + value, width);
    }

    private static string FitLine(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return value.Length <= width ? value : value[..width];
    }

    private static string FitTail(string value, int width)
    {
        if (value.Length <= width)
        {
            return value;
        }

        if (width == 1)
        {
            return "…";
        }

        return "…" + value[^Math.Max(0, width - 1)..];
    }

    private void ClearInputBuffer()
    {
        _input.Clear();
        _input.Capacity = 0;
    }
}
