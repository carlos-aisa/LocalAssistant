using System.Diagnostics;
using System.Text.Json;
using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Security.Egress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.ExternalTools;

public sealed class ControlledExternalToolsGateway : IExternalToolsGateway, IDisposable
{
    private readonly Dictionary<string, IExternalToolAdapter> _adapters;
    private readonly IEgressPolicy _egressPolicy;
    private readonly ILogger<ControlledExternalToolsGateway> _logger;
    private readonly ExternalToolsGatewayOptions _options;
    private readonly IExternalToolsGatewayDeadlineFactory _deadlineFactory;
    private readonly SemaphoreSlim _concurrency;
    private int _disposed;

    public ControlledExternalToolsGateway(
        IEnumerable<IExternalToolAdapter> adapters,
        IEgressPolicy egressPolicy,
        ILogger<ControlledExternalToolsGateway> logger,
        IOptions<ExternalToolsGatewayOptions> options,
        IExternalToolsGatewayDeadlineFactory deadlineFactory)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(egressPolicy);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadlineFactory);
        _egressPolicy = egressPolicy;
        _logger = logger;
        _options = options.Value;
        _deadlineFactory = deadlineFactory;
        if (_options.TotalTimeout <= TimeSpan.Zero ||
            _options.MaximumConcurrentOperations <= 0 ||
            _options.MaximumNormalizedResultBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _concurrency = new SemaphoreSlim(
            _options.MaximumConcurrentOperations,
            _options.MaximumConcurrentOperations);
        _adapters = new Dictionary<string, IExternalToolAdapter>(StringComparer.Ordinal);

        foreach (var adapter in adapters)
        {
            ValidateAdapter(adapter);
            if (!_adapters.TryAdd(adapter.Name, adapter))
            {
                throw new InvalidOperationException($"External tool adapter '{adapter.Name}' is registered more than once.");
            }
        }
    }

    public async ValueTask<ExternalToolGatewayResult> ExecuteAsync(
        ExternalToolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();

        if (!_adapters.TryGetValue(request.AdapterName, out var adapter))
        {
            return Failure(request, "external_adapter_not_found", start);
        }

        if (!adapter.SupportedOperations.Any(
                operation => StringComparer.Ordinal.Equals(operation, request.Operation)))
        {
            return Failure(request, "external_operation_not_allowed", start);
        }

        if (request.Fields.Select(field => field.Descriptor.Name).Distinct(StringComparer.Ordinal).Count() !=
            request.Fields.Count)
        {
            return Failure(request, "invalid_external_payload", start);
        }

        var policyRequest = new EgressRequest(
            adapter.Destination,
            request.Purpose,
            request.Fields.Select(field => field.Descriptor).ToArray());
        var decision = _egressPolicy.Evaluate(policyRequest);
        if (!decision.IsAllowed)
        {
            ExternalToolsGatewayLog.Blocked(
                _logger,
                adapter.Name,
                request.Operation,
                decision.Code.ToString());
            LogOutcome(request, "egress_denied", start);
            return new ExternalToolGatewayResult(false, null, "egress_denied", decision);
        }

        if (!_concurrency.Wait(0, CancellationToken.None))
        {
            return Failure(request, "external_gateway_busy", start, decision);
        }

        var releaseConcurrency = true;
        try
        {
            var payloadFields = request.Fields.ToDictionary(
                field => field.Descriptor.Name,
                field => field.Value.Clone(),
                StringComparer.Ordinal);

            using var deadline = _deadlineFactory.Create(_options.TotalTimeout);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);

            Task<ExternalToolAdapterResult> execution;
            try
            {
                execution = adapter.ExecuteAsync(
                    new ExternalToolPayload(request.Operation, payloadFields),
                    operationCancellation.Token).AsTask();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return Failure(request, "external_gateway_timeout", start, decision);
            }
            catch (OperationCanceledException)
            {
                return AdapterFailed(request, adapter, start, decision, "OperationCanceledException");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExternalToolsGatewayLog.AdapterFailed(
                    _logger,
                    adapter.Name,
                    request.Operation,
                    exception.GetType().Name);
                return Failure(request, "external_adapter_failed", start, decision);
            }

            ExternalToolAdapterResult adapterResult;
            try
            {
                adapterResult = await execution.WaitAsync(operationCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RetainConcurrencyUntilCompleted(execution, ref releaseConcurrency);
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                RetainConcurrencyUntilCompleted(execution, ref releaseConcurrency);
                return Failure(request, "external_gateway_timeout", start, decision);
            }
            catch (OperationCanceledException)
            {
                return AdapterFailed(request, adapter, start, decision, "OperationCanceledException");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExternalToolsGatewayLog.AdapterFailed(
                    _logger,
                    adapter.Name,
                    request.Operation,
                    exception.GetType().Name);
                return Failure(request, "external_adapter_failed", start, decision);
            }

            if (!adapterResult.IsSuccess)
            {
                return Failure(
                    request,
                    NormalizeAdapterErrorCode(adapterResult.ErrorCode),
                    start,
                    decision);
            }

            if (adapterResult.Content is null)
            {
                return Failure(request, "external_adapter_failed", start, decision);
            }

            JsonElement normalizedContent;
            byte[] normalizedBytes;
            try
            {
                normalizedContent = adapterResult.Content.Value.Clone();
                normalizedBytes = JsonSerializer.SerializeToUtf8Bytes(normalizedContent);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or JsonException)
            {
                return AdapterFailed(request, adapter, start, decision, exception.GetType().Name);
            }

            if (normalizedBytes.Length > _options.MaximumNormalizedResultBytes)
            {
                return Failure(request, "external_result_too_large", start, decision);
            }

            LogOutcome(request, "external_adapter_succeeded", start);
            return new ExternalToolGatewayResult(true, normalizedContent, null, decision);
        }
        finally
        {
            if (releaseConcurrency)
            {
                ReleaseConcurrency();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _concurrency.Dispose();
        }
    }

    private ExternalToolGatewayResult Failure(
        ExternalToolRequest request,
        string errorCode,
        long start,
        EgressDecision? decision = null)
    {
        LogOutcome(request, errorCode, start);
        return new ExternalToolGatewayResult(false, null, errorCode, decision);
    }

    private ExternalToolGatewayResult AdapterFailed(
        ExternalToolRequest request,
        IExternalToolAdapter adapter,
        long start,
        EgressDecision decision,
        string exceptionType)
    {
        ExternalToolsGatewayLog.AdapterFailed(
            _logger,
            adapter.Name,
            request.Operation,
            exceptionType);
        return Failure(request, "external_adapter_failed", start, decision);
    }

    private void LogOutcome(ExternalToolRequest request, string resultCode, long start)
    {
        ExternalToolsGatewayLog.Completed(
            _logger,
            request.AdapterName,
            request.Operation,
            resultCode,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private static string NormalizeAdapterErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) ||
            errorCode.Length > 64 ||
            errorCode.Any(character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character != '_'))
        {
            return "external_adapter_failed";
        }

        return errorCode;
    }

    private void RetainConcurrencyUntilCompleted(
        Task<ExternalToolAdapterResult> execution,
        ref bool releaseConcurrency)
    {
        if (execution.IsCompleted)
        {
            return;
        }

        releaseConcurrency = false;
        _ = ReleaseConcurrencyWhenCompletedAsync(execution);
    }

    private void ReleaseConcurrency()
    {
        try
        {
            _concurrency.Release();
        }
        catch (ObjectDisposedException)
        {
            // Service shutdown can race an adapter that ignored cancellation.
        }
    }

    private async Task ReleaseConcurrencyWhenCompletedAsync(Task<ExternalToolAdapterResult> execution)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller already received a timeout or cancellation result. The exception is
            // intentionally observed here so a non-cooperative adapter cannot become unobserved.
        }
        finally
        {
            ReleaseConcurrency();
        }
    }

    private static void ValidateAdapter(IExternalToolAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!IsSafeMetadataValue(adapter.Name, 64, requireIdentifier: true) ||
            !IsSafeMetadataValue(adapter.Destination, 512, requireIdentifier: false) ||
            adapter.SupportedOperations.Count == 0 ||
            adapter.SupportedOperations.Any(operation => !IsSafeMetadataValue(operation, 64, requireIdentifier: true)))
        {
            throw new ArgumentException("External tool adapter metadata is invalid.", nameof(adapter));
        }
    }

    private static bool IsSafeMetadataValue(string? value, int maximumLength, bool requireIdentifier)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            return false;
        }

        return !requireIdentifier || value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character is '_' or '-');
    }
}

internal static partial class ExternalToolsGatewayLog
{
    [LoggerMessage(2000, LogLevel.Warning, "External adapter {AdapterName} operation {Operation} was blocked by egress policy {DecisionCode}")]
    public static partial void Blocked(
        ILogger logger,
        string adapterName,
        string operation,
        string decisionCode);

    [LoggerMessage(2001, LogLevel.Error, "External adapter {AdapterName} operation {Operation} failed with exception type {ExceptionType}")]
    public static partial void AdapterFailed(
        ILogger logger,
        string adapterName,
        string operation,
        string exceptionType);

    [LoggerMessage(2002, LogLevel.Information, "External adapter {AdapterName} operation {Operation} completed with {ResultCode} in {DurationMilliseconds} ms")]
    public static partial void Completed(
        ILogger logger,
        string adapterName,
        string operation,
        string resultCode,
        double durationMilliseconds);
}
