using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Core.Orchestration;

public sealed class ConversationOrchestrator : IConversationOrchestrator
{
    private readonly IConversationStore _conversationStore;
    private readonly IToolRegistry _toolRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly OrchestrationOptions _options;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(
        IConversationStore conversationStore,
        IToolRegistry toolRegistry,
        TimeProvider timeProvider,
        IOptions<OrchestrationOptions> options,
        ILogger<ConversationOrchestrator> logger)
    {
        _conversationStore = conversationStore;
        _toolRegistry = toolRegistry;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;

        if (_options.MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxIterations must be greater than zero.");
        }

        if (_options.ProviderTimeout <= TimeSpan.Zero || _options.ToolTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeouts must be greater than zero.");
        }
    }

    public async Task<ConversationTurnResult> ProcessAsync(
        ConversationTurnRequest request,
        ILanguageProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("A message is required.", nameof(request));
        }

        var conversationId = request.ConversationId ?? Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();
        var providerDuration = TimeSpan.Zero;
        var toolDuration = TimeSpan.Zero;
        var toolTraces = new List<ToolExecutionTrace>();
        var iterations = 0;

        OrchestrationLog.TurnStarted(_logger, conversationId);

        await _conversationStore.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.User, request.Message),
            cancellationToken);

        for (iterations = 1; iterations <= _options.MaxIterations; iterations++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = await _conversationStore.GetMessagesAsync(conversationId, cancellationToken);
            var providerStarted = _timeProvider.GetTimestamp();

            OrchestrationLog.ProviderCalled(_logger, provider.Name, iterations, conversationId);

            LanguageProviderResponse response;
            using (var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                providerCancellation.CancelAfter(_options.ProviderTimeout);

                try
                {
                    response = await provider.GetResponseAsync(
                        new LanguageProviderRequest(conversationId, messages, _toolRegistry.Definitions),
                        providerCancellation.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    providerDuration += _timeProvider.GetElapsedTime(providerStarted);
                    OrchestrationLog.ProviderTimedOut(_logger, provider.Name, iterations, conversationId);
                    return Complete(
                        conversationId,
                        null,
                        toolTraces,
                        iterations,
                        startedAt,
                        startedTimestamp,
                        providerDuration,
                        toolDuration,
                        new OrchestrationError("provider_timeout", "The language provider timed out."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    providerDuration += _timeProvider.GetElapsedTime(providerStarted);
                    OrchestrationLog.ProviderFailed(
                        _logger,
                        provider.Name,
                        iterations,
                        conversationId,
                        exception);
                    return Complete(
                        conversationId,
                        null,
                        toolTraces,
                        iterations,
                        startedAt,
                        startedTimestamp,
                        providerDuration,
                        toolDuration,
                        new OrchestrationError("provider_error", "The language provider failed."));
                }
            }

            providerDuration += _timeProvider.GetElapsedTime(providerStarted);

            if (response.Content is not null && response.ToolCalls.Count == 0)
            {
                await _conversationStore.AppendAsync(
                    conversationId,
                    new ConversationMessage(ConversationRole.Assistant, response.Content),
                    cancellationToken);

                return Complete(
                    conversationId,
                    response.Content,
                    toolTraces,
                    iterations,
                    startedAt,
                    startedTimestamp,
                    providerDuration,
                    toolDuration);
            }

            if (response.Content is not null || response.ToolCalls.Count == 0)
            {
                return Complete(
                    conversationId,
                    null,
                    toolTraces,
                    iterations,
                    startedAt,
                    startedTimestamp,
                    providerDuration,
                    toolDuration,
                    new OrchestrationError(
                        "invalid_provider_response",
                        "The provider response must contain either final content or tool calls."));
            }

            foreach (var toolCall in response.ToolCalls)
            {
                var toolError = await ExecuteToolAsync(
                    conversationId,
                    toolCall,
                    request.ApprovedTools,
                    toolTraces,
                    elapsed => toolDuration += elapsed,
                    cancellationToken);

                if (toolError is not null)
                {
                    return Complete(
                        conversationId,
                        null,
                        toolTraces,
                        iterations,
                        startedAt,
                        startedTimestamp,
                        providerDuration,
                        toolDuration,
                        toolError);
                }
            }
        }

        return Complete(
            conversationId,
            null,
            toolTraces,
            _options.MaxIterations,
            startedAt,
            startedTimestamp,
            providerDuration,
            toolDuration,
            new OrchestrationError(
                "iteration_limit_reached",
                "The maximum number of orchestration iterations was reached."));
    }

    private async Task<OrchestrationError?> ExecuteToolAsync(
        Guid conversationId,
        ToolCall toolCall,
        IReadOnlySet<string>? approvedTools,
        List<ToolExecutionTrace> traces,
        Action<TimeSpan> addToolDuration,
        CancellationToken cancellationToken)
    {
        OrchestrationLog.ToolRequested(
            _logger,
            toolCall.Name,
            toolCall.Id,
            conversationId);

        await _conversationStore.AppendAsync(
            conversationId,
            new ConversationMessage(ConversationRole.Assistant, ToolCall: toolCall),
            cancellationToken);

        if (!_toolRegistry.TryGet(toolCall.Name, out var tool) || tool is null)
        {
            const string code = "tool_not_found";
            AddFailedTrace(traces, toolCall, code, TimeSpan.Zero);
            OrchestrationLog.ToolFailed(_logger, toolCall.Name, toolCall.Id, code, conversationId);
            return new OrchestrationError(code, "The requested tool is not registered.", toolCall.Name);
        }

        if (tool.Definition.Metadata.RequiresConfirmation &&
            (approvedTools is null || !approvedTools.Contains(toolCall.Name)))
        {
            const string code = "tool_confirmation_required";
            AddFailedTrace(traces, toolCall, code, TimeSpan.Zero);
            OrchestrationLog.ToolFailed(_logger, toolCall.Name, toolCall.Id, code, conversationId);
            return new OrchestrationError(
                code,
                "The requested tool requires explicit confirmation.",
                toolCall.Name);
        }

        var toolStarted = _timeProvider.GetTimestamp();
        ToolExecutionResult result;

        using (var toolCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            toolCancellation.CancelAfter(_options.ToolTimeout);

            try
            {
                result = await tool.ExecuteAsync(toolCall.Arguments, toolCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var elapsed = _timeProvider.GetElapsedTime(toolStarted);
                addToolDuration(elapsed);
                const string code = "tool_timeout";
                AddFailedTrace(traces, toolCall, code, elapsed);
                OrchestrationLog.ToolFailed(_logger, toolCall.Name, toolCall.Id, code, conversationId);
                return new OrchestrationError(code, "The tool timed out.", toolCall.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var elapsed = _timeProvider.GetElapsedTime(toolStarted);
                addToolDuration(elapsed);
                const string code = "tool_execution_failed";
                AddFailedTrace(traces, toolCall, code, elapsed);
                OrchestrationLog.ToolFailed(
                    _logger,
                    toolCall.Name,
                    toolCall.Id,
                    code,
                    conversationId,
                    exception);
                return new OrchestrationError(code, "The tool failed during execution.", toolCall.Name);
            }
        }

        var toolElapsed = _timeProvider.GetElapsedTime(toolStarted);
        addToolDuration(toolElapsed);

        await _conversationStore.AppendAsync(
            conversationId,
            new ConversationMessage(
                ConversationRole.Tool,
                ToolResult: new ToolResultMessage(
                    toolCall.Id,
                    toolCall.Name,
                    result.Content,
                    !result.IsSuccess)),
            cancellationToken);

        if (!result.IsSuccess)
        {
            var code = result.ErrorCode ?? "tool_execution_failed";
            AddFailedTrace(traces, toolCall, code, toolElapsed);
            OrchestrationLog.ToolFailed(_logger, toolCall.Name, toolCall.Id, code, conversationId);
            return new OrchestrationError(code, result.Content, toolCall.Name);
        }

        traces.Add(new ToolExecutionTrace(
            toolCall.Id,
            toolCall.Name,
            Succeeded: true,
            toolElapsed.TotalMilliseconds));
        OrchestrationLog.ToolCompleted(
            _logger,
            toolCall.Name,
            toolCall.Id,
            toolElapsed.TotalMilliseconds,
            conversationId);
        return null;
    }

    private static void AddFailedTrace(
        List<ToolExecutionTrace> traces,
        ToolCall toolCall,
        string code,
        TimeSpan elapsed)
    {
        traces.Add(new ToolExecutionTrace(
            toolCall.Id,
            toolCall.Name,
            Succeeded: false,
            elapsed.TotalMilliseconds,
            code));
    }

    private ConversationTurnResult Complete(
        Guid conversationId,
        string? content,
        IReadOnlyList<ToolExecutionTrace> traces,
        int iterations,
        DateTimeOffset startedAt,
        long startedTimestamp,
        TimeSpan providerDuration,
        TimeSpan toolDuration,
        OrchestrationError? error = null)
    {
        var completedAt = _timeProvider.GetUtcNow();
        var totalDuration = _timeProvider.GetElapsedTime(startedTimestamp);
        OrchestrationLog.TurnCompleted(
            _logger,
            conversationId,
            iterations,
            totalDuration.TotalMilliseconds);

        return new ConversationTurnResult(
            conversationId,
            content,
            traces,
            iterations,
            new ExecutionTimings(
                startedAt,
                completedAt,
                totalDuration.TotalMilliseconds,
                providerDuration.TotalMilliseconds,
                toolDuration.TotalMilliseconds),
            error);
    }
}
