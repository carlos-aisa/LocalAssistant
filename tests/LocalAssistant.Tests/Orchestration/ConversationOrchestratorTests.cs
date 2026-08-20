using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Tools;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Orchestration;

public sealed class ConversationOrchestratorTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 8, 17, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReturnsDirectResponseWithoutExecutingTools()
    {
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Direct answer")),
        ]);
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(store: store);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Hello"),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code + ": " + result.Error?.Message);
        Assert.Equal("Direct answer", result.Content);
        Assert.Empty(result.Tools);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(1, provider.CallCount);

        var messages = await store.GetMessagesAsync(
            result.ConversationId,
            CancellationToken.None);
        Assert.Collection(
            messages,
            message => Assert.Equal(ConversationRole.User, message.Role),
            message => Assert.Equal(ConversationRole.Assistant, message.Role));
    }

    [Fact]
    public async Task ExecutesTimeToolAndReturnsItsResultToProvider()
    {
        var timeProvider = new ManualTimeProvider(FixedUtcNow);
        var call = new ToolCall("call-time", CurrentTimeTool.ToolName, EmptyArguments());
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
            request =>
            {
                var toolResult = request.Messages.Single(message => message.ToolResult is not null);
                using var document = JsonDocument.Parse(toolResult.ToolResult!.Content);
                var utc = document.RootElement.GetProperty("utc").GetString();
                return LanguageProviderResponse.Final($"Time received: {utc}");
            },
        ]);
        var sut = CreateOrchestrator(timeProvider: timeProvider);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("What time is it?"),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code + ": " + result.Error?.Message);
        Assert.Contains("2026-08-17T14:30:00.0000000+00:00", result.Content);
        var trace = Assert.Single(result.Tools);
        Assert.Equal(CurrentTimeTool.ToolName, trace.ToolName);
        Assert.True(trace.Succeeded);
        Assert.Equal(2, result.Iterations);
    }

    [Fact]
    public async Task ReturnsStructuredErrorForUnknownTool()
    {
        var provider = ProviderRequesting(new ToolCall("missing-1", "missing", EmptyArguments()));
        var sut = CreateOrchestrator();

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Use missing tool"),
            provider,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("tool_not_found", result.Error?.Code);
        Assert.Equal("missing", result.Error?.ToolName);
        Assert.False(Assert.Single(result.Tools).Succeeded);
    }

    [Fact]
    public async Task ReturnsStructuredErrorForInvalidArguments()
    {
        var arguments = JsonSerializer.SerializeToElement(new { unexpected = true });
        var provider = ProviderRequesting(
            new ToolCall("time-invalid", CurrentTimeTool.ToolName, arguments));
        var sut = CreateOrchestrator();

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Use invalid arguments"),
            provider,
            CancellationToken.None);

        Assert.Equal("invalid_tool_arguments", result.Error?.Code);
        Assert.False(Assert.Single(result.Tools).Succeeded);
    }

    [Fact]
    public async Task ConvertsUnexpectedToolExceptionToStructuredError()
    {
        var tool = new DelegateTool(
            "explode",
            requiresConfirmation: false,
            static (_, _) => throw new InvalidOperationException("Sensitive internal detail"));
        var provider = ProviderRequesting(new ToolCall("explode-1", "explode", EmptyArguments()));
        var sut = CreateOrchestrator(tools: [tool]);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Run it"),
            provider,
            CancellationToken.None);

        Assert.Equal("tool_execution_failed", result.Error?.Code);
        Assert.DoesNotContain("Sensitive", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        var provider = new BlockingProvider();
        var sut = CreateOrchestrator(
            options: new OrchestrationOptions { ProviderTimeout = TimeSpan.FromSeconds(5) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ProcessAsync(
            new ConversationTurnRequest("Wait"),
            provider,
            cancellation.Token));
    }

    [Fact]
    public async Task ConvertsProviderTimeoutToStructuredError()
    {
        var provider = new BlockingProvider();
        var sut = CreateOrchestrator(
            options: new OrchestrationOptions { ProviderTimeout = TimeSpan.FromMilliseconds(25) });

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Wait too long"),
            provider,
            CancellationToken.None);

        Assert.Equal("provider_timeout", result.Error?.Code);
        Assert.Equal(1, result.Iterations);
    }

    [Fact]
    public async Task StopsAtConfiguredIterationLimit()
    {
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(
                new ToolCall("time-1", CurrentTimeTool.ToolName, EmptyArguments()))),
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(
                new ToolCall("time-2", CurrentTimeTool.ToolName, EmptyArguments()))),
        ]);
        var sut = CreateOrchestrator(options: new OrchestrationOptions { MaxIterations = 2 });

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Loop"),
            provider,
            CancellationToken.None);

        Assert.Equal("iteration_limit_reached", result.Error?.Code);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(2, result.Tools.Count);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task DoesNotExecuteConfirmationToolWithoutExplicitApproval()
    {
        var executions = 0;
        var tool = new DelegateTool(
            "change_state",
            requiresConfirmation: true,
            (_, _) =>
            {
                executions++;
                return ValueTask.FromResult(ToolExecutionResult.Success("changed"));
            });
        var provider = ProviderRequesting(
            new ToolCall("change-1", "change_state", EmptyArguments()));
        var sut = CreateOrchestrator(tools: [tool]);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Change something"),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Confirmation);
        Assert.Equal("change_state", result.Confirmation!.ToolName);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task ExecutesOnlyTheServerHeldToolCallAfterApproval()
    {
        var executions = 0;
        var receivedValue = 0;
        var tool = new DelegateTool("change_state", true, (arguments, _) =>
        {
            executions++;
            receivedValue = arguments.GetProperty("value").GetInt32();
            return ValueTask.FromResult(ToolExecutionResult.Success("changed"));
        });
        var call = new ToolCall("change-1", "change_state", JsonSerializer.SerializeToElement(new { value = 7 }));
        var provider = new ScriptedLanguageProvider([
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Done")),
        ]);
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(tools: [tool], store: store);

        var pending = await sut.ProcessAsync(new ConversationTurnRequest("Change"), provider, CancellationToken.None);
        var result = await sut.ResolveConfirmationAsync(pending.ConversationId, pending.Confirmation!.ConfirmationId, true, provider, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Done", result.Content);
        Assert.Equal(1, executions);
        Assert.Equal(7, receivedValue);
    }

    private static ConversationOrchestrator CreateOrchestrator(
        IEnumerable<ITool>? tools = null,
        ManualTimeProvider? timeProvider = null,
        InMemoryConversationStore? store = null,
        OrchestrationOptions? options = null)
    {
        timeProvider ??= new ManualTimeProvider(FixedUtcNow);
        tools ??= [new CurrentTimeTool(timeProvider)];

        return new ConversationOrchestrator(
            store ?? new InMemoryConversationStore(),
            new ToolRegistry(tools),
            new InMemoryToolConfirmationStore(),
            new InMemoryConversationExecutionLock(),
            timeProvider,
            Options.Create(options ?? new OrchestrationOptions()),
            NullLogger<ConversationOrchestrator>.Instance);
    }

    private static ScriptedLanguageProvider ProviderRequesting(ToolCall call)
    {
        return new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
        ]);
    }

    private static JsonElement EmptyArguments() =>
        JsonSerializer.SerializeToElement(new { });

    private sealed class BlockingProvider : ILanguageProvider
    {
        public string Name => "blocking";

        public async Task<LanguageProviderResponse> GetResponseAsync(
            LanguageProviderRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LanguageProviderResponse.Final("unreachable");
        }
    }

    private sealed class DelegateTool : ITool
    {
        private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
        });

        private readonly Func<JsonElement, CancellationToken, ValueTask<ToolExecutionResult>> _execute;

        public DelegateTool(
            string name,
            bool requiresConfirmation,
            Func<JsonElement, CancellationToken, ValueTask<ToolExecutionResult>> execute)
        {
            Definition = new ToolDefinition(
                new ToolMetadata(
                    name,
                    "Test tool",
                    requiresConfirmation ? ToolImpact.ChangesState : ToolImpact.ReadOnly,
                    requiresConfirmation),
                Schema);
            _execute = execute;
        }

        public ToolDefinition Definition { get; }

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) => _execute(arguments, cancellationToken);
    }
}
