using System.Text.Json;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Core.ExternalTools;

public interface IGatewayToolOperation
{
    ToolDefinition Definition { get; }

    ValueTask<ExternalToolRequest?> CreateRequestAsync(
        JsonElement arguments,
        CancellationToken cancellationToken);

    ToolExecutionResult CreateResult(ExternalToolGatewayResult gatewayResult);
}

public sealed class GatewayBackedTool : ITool
{
    private readonly IGatewayToolOperation _operation;
    private readonly IExternalToolsGateway _gateway;

    public GatewayBackedTool(
        IGatewayToolOperation operation,
        IExternalToolsGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(gateway);

        if (operation.Definition.Metadata.Risk.Exposure != ToolExposure.ControlledExternal)
        {
            throw new ArgumentException(
                "A gateway-backed operation must declare ControlledExternal exposure.",
                nameof(operation));
        }

        _operation = operation;
        _gateway = gateway;
    }

    public ToolDefinition Definition => _operation.Definition;

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ExternalToolRequest? request;
        try
        {
            request = await _operation.CreateRequestAsync(arguments, cancellationToken);
        }
        catch (ArgumentException)
        {
            return InvalidArguments();
        }
        catch (JsonException)
        {
            return InvalidArguments();
        }

        if (request is null)
        {
            return InvalidArguments();
        }

        var result = await _gateway.ExecuteAsync(request, cancellationToken);
        return _operation.CreateResult(result);
    }

    private static ToolExecutionResult InvalidArguments() =>
        ToolExecutionResult.Failure(
            "invalid_tool_arguments",
            "The tool arguments are invalid.",
            "The requested tool arguments are invalid.");
}
