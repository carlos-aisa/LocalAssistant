using System.Text.Json;
using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Security.Egress;
using LocalAssistant.Infrastructure.ExternalTools;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAssistant.Tests.ExternalTools;

public sealed class ControlledExternalToolsGatewayTests
{
    [Fact]
    public async Task SendsAllowedPayloadToRegisteredAdapter()
    {
        ExternalToolPayload? receivedPayload = null;
        var adapter = new DelegateAdapter((payload, _) =>
        {
            receivedPayload = payload;
            return ValueTask.FromResult(ExternalToolAdapterResult.Success(
                JsonSerializer.SerializeToElement(new { answer = "sunny" })));
        });
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("city", "Madrid", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(receivedPayload);
        Assert.Equal("search", receivedPayload!.Operation);
        Assert.Equal("Madrid", receivedPayload.Fields["city"].GetString());
        Assert.Equal("sunny", result.Content?.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task DoesNotInvokeAdapterWhenProtectedDataIsDenied()
    {
        var adapter = new DelegateAdapter();
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("document", "private", [DataCategory.LocalDocuments])),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("egress_denied", result.ErrorCode);
        Assert.Equal(EgressDecisionCode.DataCategoryDenied, result.EgressDecision?.Code);
        Assert.Equal(0, adapter.ExecutionCount);
    }

    [Fact]
    public async Task DoesNotInvokeAdapterForUnknownDataCategory()
    {
        var adapter = new DelegateAdapter();
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("value", "unknown", [new DataCategory("UNKNOWN")])),
            CancellationToken.None);

        Assert.Equal(EgressDecisionCode.UnknownDataCategory, result.EgressDecision?.Code);
        Assert.Equal(0, adapter.ExecutionCount);
    }

    [Fact]
    public async Task RejectsAdapterOutsideTheAllowlist()
    {
        var adapter = new DelegateAdapter();
        var sut = CreateGateway(adapter);

        var unknownRequest = new ExternalToolRequest(
            "unknown-adapter",
            "search",
            "test-purpose",
            [Field("query", "weather", [DataCategory.PublicData])]);
        var unknownResult = await sut.ExecuteAsync(unknownRequest, CancellationToken.None);

        Assert.Equal("external_adapter_not_found", unknownResult.ErrorCode);
        Assert.Equal(0, adapter.ExecutionCount);
    }

    [Fact]
    public async Task RejectsOperationOutsideAdapterAllowlist()
    {
        var adapter = new DelegateAdapter();
        var sut = CreateGateway(adapter);
        var request = new ExternalToolRequest(
            adapter.Name,
            "delete",
            "test-purpose",
            [Field("query", "weather", [DataCategory.PublicData])]);

        var result = await sut.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("external_operation_not_allowed", result.ErrorCode);
        Assert.Equal(0, adapter.ExecutionCount);
    }

    [Fact]
    public async Task RejectsDuplicatePayloadFieldNames()
    {
        var adapter = new DelegateAdapter();
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(
                Field("query", "weather", [DataCategory.PublicData]),
                Field("query", "forecast", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("invalid_external_payload", result.ErrorCode);
        Assert.Equal(0, adapter.ExecutionCount);
    }

    [Fact]
    public async Task ConvertsUnexpectedAdapterExceptionToSafeError()
    {
        var adapter = new DelegateAdapter(static (_, _) =>
            throw new InvalidOperationException("Sensitive provider detail"));
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_adapter_failed", result.ErrorCode);
        Assert.Equal(1, adapter.ExecutionCount);
    }

    [Fact]
    public async Task PropagatesCallerCancellationWithoutInvokingAdapter()
    {
        var adapter = new DelegateAdapter();
        var sut = CreateGateway(adapter);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.ExecuteAsync(
                Request(Field("query", "weather", [DataCategory.PublicData])),
                cancellation.Token));
        Assert.Equal(0, adapter.ExecutionCount);
    }

    private static ControlledExternalToolsGateway CreateGateway(params IExternalToolAdapter[] adapters) =>
        new(adapters, new DefaultEgressPolicy(), NullLogger<ControlledExternalToolsGateway>.Instance);

    private static ExternalToolRequest Request(params ExternalToolField[] fields) =>
        new("fake-search", "search", "test-purpose", fields);

    private static ExternalToolField Field(
        string name,
        string value,
        IReadOnlyList<DataCategory> categories) =>
        new(new EgressPayloadField(name, categories, true, true), JsonSerializer.SerializeToElement(value));

    private sealed class DelegateAdapter : IExternalToolAdapter
    {
        private readonly Func<ExternalToolPayload, CancellationToken, ValueTask<ExternalToolAdapterResult>> _execute;

        public DelegateAdapter(
            Func<ExternalToolPayload, CancellationToken, ValueTask<ExternalToolAdapterResult>>? execute = null)
        {
            _execute = execute ?? ((_, _) => ValueTask.FromResult(
                ExternalToolAdapterResult.Success(JsonSerializer.SerializeToElement(new { ok = true }))));
        }

        public string Name => "fake-search";

        public string Destination => "https://search.example.test";

        public IReadOnlySet<string> SupportedOperations { get; } = new HashSet<string>(["search"], StringComparer.Ordinal);

        public int ExecutionCount { get; private set; }

        public ValueTask<ExternalToolAdapterResult> ExecuteAsync(
            ExternalToolPayload payload,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return _execute(payload, cancellationToken);
        }
    }
}
