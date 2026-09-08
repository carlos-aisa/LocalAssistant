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
    private TerminalInputRequest? _renderedInputRequest;
    private TerminalInputRequest? _bufferedRequest;
    private CancellationTokenSource? _applicationCancellation;
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
        using var applicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _applicationCancellation = applicationCancellation;
        using var cancellationRegistration = cancellationToken.Register(CloseInputChannel);
        var applicationTask = Task.Run(() => operation(applicationCancellation.Token), CancellationToken.None);

        try
        {
            while (!applicationTask.IsCompleted)
            {
                DrainUpdates();
                ProcessAvailableInput();
                DetectResize();
                DetectInputRequestChange();
                if (_dirty)
                {
                    Render();
                }

                await Task.Delay(30, CancellationToken.None);
            }

            DrainUpdates();
            DetectResize();
            DetectInputRequestChange();
            if (_dirty)
            {
                Render();
            }

            return await applicationTask;
        }
        finally
        {
            _applicationCancellation = null;
            _console.CloseInput();
            ClearInputBuffer();
            _driver.Restore();
        }
    }

    private void CloseInputChannel()
    {
        _console.CloseInput();
        Interlocked.Exchange(ref _clearInputRequested, 1);
        _dirty = true;
    }

    private void CancelApplication()
    {
        try
        {
            _applicationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CloseInputChannel();
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
                CloseInputChannel();
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
        _console.TryGetInputRequest(out var request);
        RebindBufferTo(request);
        var intent = TerminalKeyInterpreter.Interpret(key, request is not null, _input.Length > 0);

        switch (intent.Action)
        {
            case TerminalKeyAction.CancelApplication:
                CancelApplication();
                break;
            case TerminalKeyAction.CloseChannel:
                CloseInputChannel();
                break;
            case TerminalKeyAction.Scroll:
                _scrollOffset = Math.Clamp(
                    _scrollOffset + intent.ScrollDelta,
                    0,
                    TerminalClientTuiTranscript.MaximumReferenceLines);
                _dirty = true;
                break;
            case TerminalKeyAction.Submit:
                SubmitBuffer(_input.ToString());
                break;
            case TerminalKeyAction.SubmitEmpty:
                SubmitBuffer(string.Empty);
                break;
            case TerminalKeyAction.DeletePrevious:
                _input.Length--;
                _dirty = true;
                break;
            case TerminalKeyAction.Insert:
                _input.Append(intent.Character);
                _dirty = true;
                break;
            case TerminalKeyAction.Ignore:
                break;
        }
    }

    private void SubmitBuffer(string value)
    {
        ClearInputBuffer();
        _bufferedRequest = null;
        _console.CompleteInput(value);
        _dirty = true;
    }

    private void RebindBufferTo(TerminalInputRequest? request)
    {
        if (ReferenceEquals(request, _bufferedRequest))
        {
            return;
        }

        ClearInputBuffer();
        _bufferedRequest = request;
    }

    private void DetectResize()
    {
        if (_driver.GetSize() != _lastSize)
        {
            _dirty = true;
        }
    }

    private void DetectInputRequestChange()
    {
        // A new pending prompt must be painted even when nothing else changed (consecutive
        // reads such as client id then credential). Clearing a prompt does not force a frame:
        // the application either publishes the next prompt or a state transition follows.
        _console.TryGetInputRequest(out var request);
        if (request is not null && !ReferenceEquals(request, _renderedInputRequest))
        {
            _dirty = true;
        }

        _renderedInputRequest = request;
    }

    private void Render()
    {
        _dirty = false;
        // Always paint against the current size. A priority snapshot drained after a
        // resize but before DetectResize must not be rendered with the stale cached size.
        var size = _driver.GetSize();
        _lastSize = size;
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var lines = width < MinimumWidth || height < MinimumHeight
            ? CreateCompactFrame(width, height)
            : CreateFrame(width, height);

        _driver.Render(lines);
    }

    private List<string> CreateFrame(int width, int height)
    {
        // At the compatible minimum (40x8) the priority rows always fit, so their order
        // here is purely visual: transcript on top, then state, error, confirmation, and
        // the input line adjacent to the bottom edge. The Take(height) is a defensive cap.
        var priorityLines = new List<string>();
        priorityLines.Add(FitLine(CreateStateLine(), width));
        AddErrorLine(priorityLines, width);
        AddConfirmationLine(priorityLines, width);
        AddInputLine(priorityLines, width);

        var transcriptHeight = Math.Max(0, height - priorityLines.Count);
        var view = _transcript.CreateView(width, transcriptHeight, _scrollOffset);
        _scrollOffset = view.ClampedScrollOffset;
        return view.Lines.Concat(priorityLines).Take(height).ToList();
    }

    private List<string> CreateCompactFrame(int width, int height)
    {
        // Below the compatible minimum, rows are dropped by retention priority
        // (confirmation, input, resize hint, error, state); Take(height) keeps the top.
        var lines = new List<string>();
        AddConfirmationLine(lines, width);
        AddInputLine(lines, width);
        lines.Add(FitLine("Terminal too small. Resize to at least 40x8.", width));
        AddErrorLine(lines, width);
        lines.Add(FitLine(CreateStateLine(), width));
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
        var spokenOutput = _snapshot.Activity == TerminalClientActivity.PlayingVoice
            ? "playing"
            : _snapshot.SpokenOutput.Availability == SpokenOutputAvailability.Unavailable
                ? "unavailable"
                : _snapshot.SpokenOutput.IsMuted
                    ? "muted"
                    : "ready";
        return $"State: {_snapshot.Lifecycle}/{_snapshot.Activity}; Provider: {provider}; Conversation: {conversation}; Speech: {spokenOutput}";
    }

    private static string FitInputLine(string prompt, string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (prompt.Length >= width)
        {
            // The prompt alone overflows. Keep a recognizable head of the prompt while
            // nothing is typed; once the user types, show the active tail of the value so
            // the editing position stays visible. Never render an empty input line.
            return value.Length == 0 ? FitHead(prompt, width) : FitTail(value, width);
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

    private static string FitHead(string value, int width)
    {
        if (value.Length <= width)
        {
            return value;
        }

        return width == 1 ? "…" : value[..(width - 1)] + "…";
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
