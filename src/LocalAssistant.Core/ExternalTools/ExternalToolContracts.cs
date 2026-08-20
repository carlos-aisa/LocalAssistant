using System.Text.Json;
using LocalAssistant.Core.Security.Egress;

namespace LocalAssistant.Core.ExternalTools;

public sealed record ExternalToolField
{
    public ExternalToolField(EgressPayloadField descriptor, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        Value = value.Clone();
    }

    public EgressPayloadField Descriptor { get; }

    public JsonElement Value { get; }
}

public sealed record ExternalToolRequest
{
    public ExternalToolRequest(
        string adapterName,
        string operation,
        string purpose,
        IReadOnlyList<ExternalToolField> fields)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            throw new ArgumentException("An adapter name is required.", nameof(adapterName));
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("An operation is required.", nameof(operation));
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException("A purpose is required.", nameof(purpose));
        }

        ArgumentNullException.ThrowIfNull(fields);
        AdapterName = adapterName;
        Operation = operation;
        Purpose = purpose;
        Fields = [.. fields];
    }

    public string AdapterName { get; }

    public string Operation { get; }

    public string Purpose { get; }

    public IReadOnlyList<ExternalToolField> Fields { get; }
}

public sealed record ExternalToolPayload(
    string Operation,
    IReadOnlyDictionary<string, JsonElement> Fields);

public sealed record ExternalToolAdapterResult(
    bool IsSuccess,
    JsonElement? Content,
    string? ErrorCode = null)
{
    public static ExternalToolAdapterResult Success(JsonElement content) =>
        new(true, content.Clone());

    public static ExternalToolAdapterResult Failure(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("An error code is required.", nameof(errorCode));
        }

        return new ExternalToolAdapterResult(false, null, errorCode);
    }
}

public sealed record ExternalToolGatewayResult(
    bool IsSuccess,
    JsonElement? Content,
    string? ErrorCode,
    EgressDecision? EgressDecision);

public interface IExternalToolAdapter
{
    string Name { get; }

    string Destination { get; }

    IReadOnlySet<string> SupportedOperations { get; }

    ValueTask<ExternalToolAdapterResult> ExecuteAsync(
        ExternalToolPayload payload,
        CancellationToken cancellationToken);
}

public interface IExternalToolsGateway
{
    ValueTask<ExternalToolGatewayResult> ExecuteAsync(
        ExternalToolRequest request,
        CancellationToken cancellationToken);
}
