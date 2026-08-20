using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Core.Orchestration;

#pragma warning disable CA1725
public sealed class ConversationOrchestrator : IConversationOrchestrator
{
    private readonly IConversationStore _store;
    private readonly IToolRegistry _tools;
    private readonly IToolConfirmationStore _confirmations;
    private readonly IConversationExecutionLock _locks;
    private readonly TimeProvider _clock;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(IConversationStore store, IToolRegistry tools, IToolConfirmationStore confirmations, IConversationExecutionLock locks, TimeProvider clock, IOptions<OrchestrationOptions> options, ILogger<ConversationOrchestrator> logger)
    {
        _store = store; _tools = tools; _confirmations = confirmations; _locks = locks; _clock = clock; _options = options.Value; _logger = logger;
        if (_options.MaxIterations <= 0 || _options.ProviderTimeout <= TimeSpan.Zero || _options.ToolTimeout <= TimeSpan.Zero || _options.ConfirmationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<ConversationTurnResult> ProcessAsync(ConversationTurnRequest request, ILanguageProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(request.Message)) throw new ArgumentException("A message is required.", nameof(request));
        var id = request.ConversationId ?? Guid.NewGuid();
        using var lease = await _locks.AcquireAsync(id, ct);
        if (await _confirmations.GetAsync(id, ct) is not null) return Result(id, null, [], 0, TimeSpan.Zero, TimeSpan.Zero, new("confirmation_pending", "A tool confirmation is already pending."));
        await _store.AppendAsync(id, new(ConversationRole.User, request.Message), ct);
        return await ContinueAsync(id, provider, [], 0, TimeSpan.Zero, ct);
    }

    public async Task<ConversationTurnResult> ResolveConfirmationAsync(Guid id, Guid confirmationId, bool approved, ILanguageProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using var lease = await _locks.AcquireAsync(id, ct);
        var pending = await _confirmations.GetAsync(id, ct);
        if (pending is null || pending.ConfirmationId != confirmationId) return Result(id, null, [], 0, TimeSpan.Zero, TimeSpan.Zero, new("confirmation_not_found", "The confirmation was not found."));
        if (!StringComparer.Ordinal.Equals(pending.ProviderName, provider.Name)) return Result(id, null, [], 0, TimeSpan.Zero, TimeSpan.Zero, new("confirmation_provider_mismatch", "The confirmation must use its original provider."));
        pending = await _confirmations.TakeAsync(id, confirmationId, ct);
        if (pending is null) return Result(id, null, [], 0, TimeSpan.Zero, TimeSpan.Zero, new("confirmation_not_found", "The confirmation was not found."));
        if (pending.ExpiresAtUtc <= _clock.GetUtcNow())
        { await RejectAsync(id, pending.ToolCall, "The tool confirmation expired.", ct); return Result(id, null, [Failed(pending.ToolCall, "confirmation_expired")], 0, TimeSpan.Zero, TimeSpan.Zero, new("confirmation_expired", "The confirmation expired.")); }
        var traces = new List<ToolExecutionTrace>(); var toolsTime = TimeSpan.Zero;
        if (approved)
        { var error = await ExecuteAsync(id, pending.ToolCall, traces, e => toolsTime += e, ct); if (error is not null) return Result(id, null, traces, 0, TimeSpan.Zero, toolsTime, error); }
        else { await RejectAsync(id, pending.ToolCall, "The user rejected this tool call.", ct); traces.Add(Failed(pending.ToolCall, "tool_confirmation_rejected")); }
        foreach (var call in pending.RemainingToolCalls)
        { var outcome = await HandleAsync(id, provider.Name, call, [], traces, e => toolsTime += e, ct); if (outcome.Error is not null || outcome.Confirmation is not null) return Result(id, null, traces, 0, TimeSpan.Zero, toolsTime, outcome.Error, outcome.Confirmation); }
        return await ContinueAsync(id, provider, traces, 0, toolsTime, ct);
    }

    private async Task<ConversationTurnResult> ContinueAsync(Guid id, ILanguageProvider provider, List<ToolExecutionTrace> traces, int completedIterations, TimeSpan toolsTime, CancellationToken ct)
    {
        var providerTime = TimeSpan.Zero;
        for (var iteration = completedIterations + 1; iteration <= _options.MaxIterations; iteration++)
        {
            LanguageProviderResponse response; var start = _clock.GetTimestamp();
            try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(_options.ProviderTimeout); response = await provider.GetResponseAsync(new(id, await _store.GetMessagesAsync(id, ct), _tools.Definitions), timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { providerTime += _clock.GetElapsedTime(start); return Result(id, null, traces, iteration, providerTime, toolsTime, new("provider_timeout", "The language provider timed out.")); }
            catch (Exception exception) when (exception is not OperationCanceledException) { providerTime += _clock.GetElapsedTime(start); OrchestrationLog.ProviderFailed(_logger, provider.Name, iteration, id, exception); return Result(id, null, traces, iteration, providerTime, toolsTime, new("provider_error", "The language provider failed.")); }
            providerTime += _clock.GetElapsedTime(start);
            if (response.Content is not null && response.ToolCalls.Count == 0) { await _store.AppendAsync(id, new(ConversationRole.Assistant, response.Content), ct); return Result(id, response.Content, traces, iteration, providerTime, toolsTime, null); }
            if (response.Content is not null || response.ToolCalls.Count == 0) return Result(id, null, traces, iteration, providerTime, toolsTime, new("invalid_provider_response", "The provider response must contain either final content or tool calls."));
            for (var index = 0; index < response.ToolCalls.Count; index++)
            { var outcome = await HandleAsync(id, provider.Name, response.ToolCalls[index], response.ToolCalls.Skip(index + 1).ToArray(), traces, e => toolsTime += e, ct); if (outcome.Error is not null || outcome.Confirmation is not null) return Result(id, null, traces, iteration, providerTime, toolsTime, outcome.Error, outcome.Confirmation); }
        }
        return Result(id, null, traces, _options.MaxIterations, providerTime, toolsTime, new("iteration_limit_reached", "The maximum number of orchestration iterations was reached."));
    }

    private async Task<(OrchestrationError? Error, ToolConfirmationRequest? Confirmation)> HandleAsync(Guid id, string providerName, ToolCall call, IReadOnlyList<ToolCall> remaining, List<ToolExecutionTrace> traces, Action<TimeSpan> addTime, CancellationToken ct)
    {
        await _store.AppendAsync(id, new(ConversationRole.Assistant, ToolCall: call), ct);
        if (!_tools.TryGet(call.Name, out var tool) || tool is null) { traces.Add(Failed(call, "tool_not_found")); return (new("tool_not_found", "The requested tool is not registered.", call.Name), null); }
        if (tool.Definition.Metadata.RequiresConfirmation)
        { var pending = new PendingToolConfirmation(Guid.NewGuid(), id, providerName, call, remaining, _clock.GetUtcNow().Add(_options.ConfirmationTimeout)); await _confirmations.CreateAsync(pending, ct); return (null, new(pending.ConfirmationId, call.Id, call.Name, call.Arguments, pending.ExpiresAtUtc)); }
        return (await ExecuteAsync(id, call, traces, addTime, ct), null);
    }

    private async Task<OrchestrationError?> ExecuteAsync(Guid id, ToolCall call, List<ToolExecutionTrace> traces, Action<TimeSpan> addTime, CancellationToken ct)
    {
        if (!_tools.TryGet(call.Name, out var tool) || tool is null) { traces.Add(Failed(call, "tool_not_found")); return new("tool_not_found", "The requested tool is not registered.", call.Name); }
        var start = _clock.GetTimestamp(); ToolExecutionResult result;
        try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(_options.ToolTimeout); result = await tool.ExecuteAsync(call.Arguments, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { var elapsed = _clock.GetElapsedTime(start); addTime(elapsed); traces.Add(Failed(call, "tool_timeout", elapsed)); return new("tool_timeout", "The tool timed out.", call.Name); }
        catch (Exception exception) when (exception is not OperationCanceledException) { var elapsed = _clock.GetElapsedTime(start); addTime(elapsed); traces.Add(Failed(call, "tool_execution_failed", elapsed)); OrchestrationLog.ToolFailed(_logger, call.Name, call.Id, "tool_execution_failed", id, exception); return new("tool_execution_failed", "The tool failed during execution.", call.Name); }
        var duration = _clock.GetElapsedTime(start); addTime(duration);
        await _store.AppendAsync(id, new(ConversationRole.Tool, ToolResult: new(call.Id, call.Name, result.Content, !result.IsSuccess)), ct);
        if (!result.IsSuccess) { var code = result.ErrorCode ?? "tool_execution_failed"; traces.Add(Failed(call, code, duration)); return new(code, result.Content, call.Name); }
        traces.Add(new(call.Id, call.Name, true, duration.TotalMilliseconds)); return null;
    }

    private async Task RejectAsync(Guid id, ToolCall call, string message, CancellationToken ct) => await _store.AppendAsync(id, new(ConversationRole.Tool, ToolResult: new(call.Id, call.Name, message, true)), ct);
    private static ToolExecutionTrace Failed(ToolCall call, string code, TimeSpan elapsed = default) => new(call.Id, call.Name, false, elapsed.TotalMilliseconds, code);
    private ConversationTurnResult Result(Guid id, string? content, IReadOnlyList<ToolExecutionTrace> traces, int iterations, TimeSpan providerTime, TimeSpan toolsTime, OrchestrationError? error, ToolConfirmationRequest? confirmation = null)
    { var now = _clock.GetUtcNow(); return new(id, content, traces, iterations, new(now, now, providerTime.TotalMilliseconds + toolsTime.TotalMilliseconds, providerTime.TotalMilliseconds, toolsTime.TotalMilliseconds), error, confirmation); }
}
#pragma warning restore CA1725
