using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Security.Egress;
using Microsoft.Extensions.Logging;

namespace LocalAssistant.Infrastructure.ExternalTools;

public sealed class ControlledExternalToolsGateway : IExternalToolsGateway
{
    private readonly Dictionary<string, IExternalToolAdapter> _adapters;
    private readonly IEgressPolicy _egressPolicy;
    private readonly ILogger<ControlledExternalToolsGateway> _logger;

    public ControlledExternalToolsGateway(
        IEnumerable<IExternalToolAdapter> adapters,
        IEgressPolicy egressPolicy,
        ILogger<ControlledExternalToolsGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(egressPolicy);
        ArgumentNullException.ThrowIfNull(logger);
        _egressPolicy = egressPolicy;
        _logger = logger;
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

        if (!_adapters.TryGetValue(request.AdapterName, out var adapter))
        {
            return Failure("external_adapter_not_found");
        }

        if (!adapter.SupportedOperations.Any(
                operation => StringComparer.Ordinal.Equals(operation, request.Operation)))
        {
            return Failure("external_operation_not_allowed");
        }

        if (request.Fields.Select(field => field.Descriptor.Name).Distinct(StringComparer.Ordinal).Count() !=
            request.Fields.Count)
        {
            return Failure("invalid_external_payload");
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
            return new ExternalToolGatewayResult(false, null, "egress_denied", decision);
        }

        var payloadFields = request.Fields.ToDictionary(
            field => field.Descriptor.Name,
            field => field.Value.Clone(),
            StringComparer.Ordinal);

        ExternalToolAdapterResult adapterResult;
        try
        {
            adapterResult = await adapter.ExecuteAsync(
                new ExternalToolPayload(request.Operation, payloadFields),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExternalToolsGatewayLog.AdapterFailed(
                _logger,
                adapter.Name,
                request.Operation,
                exception);
            return new ExternalToolGatewayResult(false, null, "external_adapter_failed", decision);
        }

        if (!adapterResult.IsSuccess)
        {
            return new ExternalToolGatewayResult(
                false,
                null,
                adapterResult.ErrorCode ?? "external_adapter_failed",
                decision);
        }

        return new ExternalToolGatewayResult(
            true,
            adapterResult.Content?.Clone(),
            null,
            decision);
    }

    private static ExternalToolGatewayResult Failure(string errorCode) =>
        new(false, null, errorCode, null);

    private static void ValidateAdapter(IExternalToolAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.Name) ||
            string.IsNullOrWhiteSpace(adapter.Destination) ||
            adapter.SupportedOperations.Count == 0 ||
            adapter.SupportedOperations.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("External tool adapter metadata is invalid.", nameof(adapter));
        }
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

    [LoggerMessage(2001, LogLevel.Error, "External adapter {AdapterName} operation {Operation} failed")]
    public static partial void AdapterFailed(
        ILogger logger,
        string adapterName,
        string operation,
        Exception exception);
}
