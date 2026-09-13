using System.Text.Json;
using LocalAssistant.Core.ExternalTools;
using LocalAssistant.Core.Security.Egress;
using LocalAssistant.Infrastructure.ExternalTools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var logger = new CapturingLogger<ControlledExternalToolsGateway>();
        var sut = CreateGateway(logger, new ExternalToolsGatewayOptions(), adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_adapter_failed", result.ErrorCode);
        Assert.Equal(1, adapter.ExecutionCount);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("Sensitive provider detail", StringComparison.Ordinal));
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

    [Fact]
    public async Task AppliesTheGatewayTotalTimeoutToAnAdapter()
    {
        using var deadlineFactory = new ManualDeadlineFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ExternalToolAdapterResult.Failure("unreachable");
        });
        var sut = CreateGateway(
            NullLogger<ControlledExternalToolsGateway>.Instance,
            new ExternalToolsGatewayOptions(),
            deadlineFactory,
            adapter);

        var execution = sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None).AsTask();
        await started.Task;
        deadlineFactory.CancelCurrentDeadline();

        var result = await execution;

        Assert.Equal("external_gateway_timeout", result.ErrorCode);
        Assert.Equal(1, adapter.ExecutionCount);
    }

    [Fact]
    public async Task RetainsConcurrencyWhenATimedOutAdapterIgnoresCancellation()
    {
        using var deadlineFactory = new ManualDeadlineFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter(async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return ExternalToolAdapterResult.Success(JsonSerializer.SerializeToElement(new { ok = true }));
        });
        var sut = CreateGateway(
            NullLogger<ControlledExternalToolsGateway>.Instance,
            new ExternalToolsGatewayOptions(),
            deadlineFactory,
            adapter);

        var first = sut.ExecuteAsync(
            Request(Field("query", "first", [DataCategory.PublicData])),
            CancellationToken.None).AsTask();
        await started.Task;
        deadlineFactory.CancelCurrentDeadline();

        var timedOut = await first;
        var rejected = await sut.ExecuteAsync(
            Request(Field("query", "second", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_gateway_timeout", timedOut.ErrorCode);
        Assert.Equal("external_gateway_busy", rejected.ErrorCode);
        Assert.Equal(1, adapter.ExecutionCount);

        release.TrySetResult();
    }

    [Fact]
    public async Task PropagatesCancellationAfterTheAdapterStarts()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ExternalToolAdapterResult.Failure("unreachable");
        });
        var sut = CreateGateway(adapter);
        using var cancellation = new CancellationTokenSource();

        var execution = sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(1, adapter.ExecutionCount);
    }

    [Fact]
    public async Task RejectsConcurrentWorkWithoutQueuingIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ExternalToolAdapterResult.Success(JsonSerializer.SerializeToElement(new { ok = true }));
        });
        var sut = CreateGateway(
            NullLogger<ControlledExternalToolsGateway>.Instance,
            new ExternalToolsGatewayOptions { MaximumConcurrentOperations = 1 },
            adapter);

        var first = sut.ExecuteAsync(
            Request(Field("query", "first", [DataCategory.PublicData])),
            CancellationToken.None).AsTask();
        await started.Task;

        var second = await sut.ExecuteAsync(
            Request(Field("query", "second", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_gateway_busy", second.ErrorCode);
        Assert.Equal(1, adapter.ExecutionCount);

        release.TrySetResult();
        Assert.True((await first).IsSuccess);
    }

    [Fact]
    public async Task RejectsAResultWhoseNormalizedRepresentationExceedsTheLimit()
    {
        var adapter = new DelegateAdapter(static (_, _) => ValueTask.FromResult(
            ExternalToolAdapterResult.Success(JsonSerializer.SerializeToElement(new
            {
                content = new string('x', 100),
            }))));
        var sut = CreateGateway(
            NullLogger<ControlledExternalToolsGateway>.Instance,
            new ExternalToolsGatewayOptions { MaximumNormalizedResultBytes = 32 },
            adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_result_too_large", result.ErrorCode);
        Assert.Equal(1, adapter.ExecutionCount);
    }

    [Fact]
    public async Task AuditsSafeAdapterAndOperationOutcomeWithoutPayloadOrExceptionMessage()
    {
        var logger = new CapturingLogger<ControlledExternalToolsGateway>();
        var adapter = new DelegateAdapter(static (_, _) =>
            throw new InvalidOperationException("https://sensitive.example.test/?coordinates=40.4"));
        var sut = CreateGateway(logger, new ExternalToolsGatewayOptions(), adapter);

        await sut.ExecuteAsync(
            Request(Field("query", "private value", [DataCategory.PublicData])),
            CancellationToken.None);

        var audit = Assert.Single(logger.Entries, entry => entry.Contains("completed with", StringComparison.Ordinal));
        Assert.Contains(adapter.Name, audit, StringComparison.Ordinal);
        Assert.Contains("search", audit, StringComparison.Ordinal);
        Assert.Contains("external_adapter_failed", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("private value", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive.example.test", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizesAnUnsafeAdapterErrorCodeBeforeReturningOrAuditingIt()
    {
        var logger = new CapturingLogger<ControlledExternalToolsGateway>();
        var adapter = new DelegateAdapter(static (_, _) => ValueTask.FromResult(
            ExternalToolAdapterResult.Failure("provider error: https://sensitive.example.test")));
        var sut = CreateGateway(logger, new ExternalToolsGatewayOptions(), adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_adapter_failed", result.ErrorCode);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive.example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertsAdapterOwnedCancellationToASafeAdapterFailure()
    {
        var adapter = new DelegateAdapter(static (_, _) =>
            ValueTask.FromCanceled<ExternalToolAdapterResult>(new CancellationToken(canceled: true)));
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_adapter_failed", result.ErrorCode);
    }

    [Fact]
    public async Task ConvertsAnInvalidSuccessfulAdapterResultToASafeAdapterFailure()
    {
        var adapter = new DelegateAdapter(static (_, _) => ValueTask.FromResult(
            new ExternalToolAdapterResult(true, default(JsonElement))));
        var sut = CreateGateway(adapter);

        var result = await sut.ExecuteAsync(
            Request(Field("query", "weather", [DataCategory.PublicData])),
            CancellationToken.None);

        Assert.Equal("external_adapter_failed", result.ErrorCode);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("bad\nname")]
    [InlineData("UPPERCASE")]
    public void RejectsUnsafeAdapterNames(string name)
    {
        Assert.Throws<ArgumentException>(() => CreateGateway(new MetadataAdapter(name, "search")));
    }

    [Theory]
    [InlineData("bad operation")]
    [InlineData("bad\noperation")]
    [InlineData("UPPERCASE")]
    public void RejectsUnsafeAdapterOperations(string operation)
    {
        Assert.Throws<ArgumentException>(() => CreateGateway(new MetadataAdapter("fake_search", operation)));
    }

    [Fact]
    public void RejectsAdapterMetadataContainingControlCharactersOrExcessiveLengths()
    {
        Assert.Throws<ArgumentException>(() => CreateGateway(new MetadataAdapter(
            "fake_search",
            "search",
            destination: "https://example.test\u001b")));
        Assert.Throws<ArgumentException>(() => CreateGateway(new MetadataAdapter(
            new string('a', 65),
            "search")));
        Assert.Throws<ArgumentException>(() => CreateGateway(new MetadataAdapter(
            "fake_search",
            new string('a', 65))));
        Assert.Throws<ArgumentException>(() => CreateGateway(new MetadataAdapter(
            "fake_search",
            "search",
            destination: new string('x', 513))));
    }

    private static ControlledExternalToolsGateway CreateGateway(params IExternalToolAdapter[] adapters) =>
        CreateGateway(
            NullLogger<ControlledExternalToolsGateway>.Instance,
            new ExternalToolsGatewayOptions(),
            adapters);

    private static ControlledExternalToolsGateway CreateGateway(
        ILogger<ControlledExternalToolsGateway> logger,
        ExternalToolsGatewayOptions options,
        params IExternalToolAdapter[] adapters) =>
        CreateGateway(logger, options, new TimeProviderExternalToolsGatewayDeadlineFactory(TimeProvider.System), adapters);

    private static ControlledExternalToolsGateway CreateGateway(
        ILogger<ControlledExternalToolsGateway> logger,
        ExternalToolsGatewayOptions options,
        IExternalToolsGatewayDeadlineFactory deadlineFactory,
        params IExternalToolAdapter[] adapters) =>
        new(
            adapters,
            new DefaultEgressPolicy(),
            logger,
            Options.Create(options),
            deadlineFactory);

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

    private sealed class MetadataAdapter : IExternalToolAdapter
    {
        public MetadataAdapter(string name, string operation, string? destination = null)
        {
            Name = name;
            Destination = destination ?? "https://search.example.test";
            SupportedOperations = new HashSet<string>([operation], StringComparer.Ordinal);
        }

        public string Name { get; }

        public string Destination { get; }

        public IReadOnlySet<string> SupportedOperations { get; }

        public ValueTask<ExternalToolAdapterResult> ExecuteAsync(
            ExternalToolPayload payload,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ManualDeadlineFactory : IExternalToolsGatewayDeadlineFactory, IDisposable
    {
        private CancellationTokenSource? _currentDeadline;

        public CancellationTokenSource Create(TimeSpan timeout)
        {
            _currentDeadline = new CancellationTokenSource();
            return _currentDeadline;
        }

        public void CancelCurrentDeadline()
        {
            Assert.NotNull(_currentDeadline);
            _currentDeadline!.Cancel();
        }

        public void Dispose()
        {
            _currentDeadline?.Dispose();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
