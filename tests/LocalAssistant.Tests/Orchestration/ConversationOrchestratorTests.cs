using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Security.ToolRisk;
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
    public async Task SendsTheCurrentAssistantProfileAsNonPersistedSystemContextOnEveryProviderCall()
    {
        var profiles = new MutableAssistantProfileStore("Jarvis");
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(
                new ToolCall("call-1", CurrentTimeTool.ToolName, EmptyArguments()))),
            request =>
            {
                var systemMessage = Assert.Single(
                    request.Messages,
                    message => message.Role == ConversationRole.System &&
                               message.Content!.Contains("configured display name", StringComparison.Ordinal));
                Assert.Contains("Jarvis", systemMessage.Content, StringComparison.Ordinal);
                return LanguageProviderResponse.Final("It is 14:30 UTC.");
            },
        ]);
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(store: store, profiles: profiles);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("What time is it?"),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var storedMessages = await store.GetMessagesAsync(result.ConversationId, CancellationToken.None);
        Assert.DoesNotContain(storedMessages, message => message.Role == ConversationRole.System);
    }

    [Fact]
    public async Task AddsRetrievedPrivateContextAsNonPersistedSystemMessage()
    {
        var owner = new ToolPolicyContext(
            "owner-a",
            new HashSet<string>(StringComparer.Ordinal));
        var contextRetriever = new StaticConversationContextRetriever(
            new ConversationRetrievedContext(
                Guid.NewGuid(),
                FixedUtcNow.AddDays(-1),
                "Week menus",
                "We planned weekday meals.",
                "Monday pasta.",
                1));
        var provider = new ScriptedLanguageProvider(
        [
            request =>
            {
                Assert.Contains(
                    request.Messages,
                    message => message.Role == ConversationRole.System &&
                               message.Content!.Contains("Week menus", StringComparison.Ordinal));
                return LanguageProviderResponse.Final("Let's continue with the menus.");
            },
        ]);
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(
            store: store,
            toolPolicyContextAccessor: new MutableToolPolicyContextAccessor(owner),
            conversationContextRetriever: contextRetriever);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Tengo más ideas para los menús de la semana."),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, contextRetriever.CallCount);
        var history = await store.GetMessagesAsync(result.ConversationId, CancellationToken.None);
        Assert.DoesNotContain(history, message => message.Role == ConversationRole.System);
    }

    [Fact]
    public async Task RefreshesTheAssistantProfileAfterAnApprovedNameChange()
    {
        var profiles = new MutableAssistantProfileStore(AssistantProfile.DefaultDisplayName);
        var policyContext = new ToolPolicyContext(
            "owner",
            new HashSet<string>(StringComparer.Ordinal) { "installation.owner" });
        var toolCall = new ToolCall(
            "set-name",
            SetAssistantNameTool.ToolName,
            JsonSerializer.SerializeToElement(new { displayName = "Jarvis" }));
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(toolCall)),
            request =>
            {
                var systemMessage = Assert.Single(
                    request.Messages,
                    message => message.Role == ConversationRole.System);
                Assert.Contains("Jarvis", systemMessage.Content, StringComparison.Ordinal);
                return LanguageProviderResponse.Final("My name is Jarvis.");
            },
        ]);
        var sut = CreateOrchestrator(
            tools: [new SetAssistantNameTool(profiles)],
            toolPolicyContextAccessor: new MutableToolPolicyContextAccessor(policyContext),
            profiles: profiles);

        var pending = await sut.ProcessAsync(
            new ConversationTurnRequest("Call yourself Jarvis."),
            provider,
            CancellationToken.None);
        var completed = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            approved: true,
            provider,
            CancellationToken.None);

        Assert.True(completed.IsSuccess);
        Assert.Equal("Jarvis", (await profiles.GetAsync(CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task RejectingAnAssistantNameChangeKeepsTheExistingProfile()
    {
        var profiles = new MutableAssistantProfileStore(AssistantProfile.DefaultDisplayName);
        var policyContext = new ToolPolicyContext(
            "owner",
            new HashSet<string>(StringComparer.Ordinal) { "installation.owner" });
        var toolCall = new ToolCall(
            "set-name",
            SetAssistantNameTool.ToolName,
            JsonSerializer.SerializeToElement(new { displayName = "Jarvis" }));
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(toolCall)),
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("No change was made.")),
        ]);
        var sut = CreateOrchestrator(
            tools: [new SetAssistantNameTool(profiles)],
            toolPolicyContextAccessor: new MutableToolPolicyContextAccessor(policyContext),
            profiles: profiles);

        var pending = await sut.ProcessAsync(
            new ConversationTurnRequest("Call yourself Jarvis."),
            provider,
            CancellationToken.None);
        var result = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            approved: false,
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AssistantProfile.DefaultDisplayName, (await profiles.GetAsync(CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task DenyingAssistantNameScopeKeepsTheExistingProfile()
    {
        var profiles = new MutableAssistantProfileStore(AssistantProfile.DefaultDisplayName);
        var toolCall = new ToolCall(
            "set-name",
            SetAssistantNameTool.ToolName,
            JsonSerializer.SerializeToElement(new { displayName = "Jarvis" }));
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(toolCall)),
        ]);
        var sut = CreateOrchestrator(
            tools: [new SetAssistantNameTool(profiles)],
            toolPolicyContextAccessor: new MutableToolPolicyContextAccessor(
                new ToolPolicyContext("authenticated-user", new HashSet<string>(StringComparer.Ordinal))),
            profiles: profiles);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Call yourself Jarvis."),
            provider,
            CancellationToken.None);

        Assert.Equal("scope_not_granted", result.Error?.Code);
        Assert.Equal(AssistantProfile.DefaultDisplayName, (await profiles.GetAsync(CancellationToken.None)).DisplayName);
    }

    [Fact]
    public async Task DoesNotContinueOwnedConversationForAnotherPrincipal()
    {
        var owner = new ToolPolicyContext(
            "owner-principal",
            new HashSet<string>(StringComparer.Ordinal));
        var otherPrincipal = new ToolPolicyContext(
            "other-principal",
            new HashSet<string>(StringComparer.Ordinal));
        var contextAccessor = new MutableToolPolicyContextAccessor(owner);
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(
            store: store,
            toolPolicyContextAccessor: contextAccessor);
        var ownerProvider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Owner answer")),
        ]);

        var ownedTurn = await sut.ProcessAsync(
            new ConversationTurnRequest("Private conversation"),
            ownerProvider,
            CancellationToken.None);

        contextAccessor.Current = otherPrincipal;
        var otherProvider = new ScriptedLanguageProvider([]);
        var rejectedTurn = await sut.ProcessAsync(
            new ConversationTurnRequest("Attempted access", ownedTurn.ConversationId),
            otherProvider,
            CancellationToken.None);

        Assert.Equal("conversation_not_found", rejectedTurn.Error?.Code);
        Assert.Equal(0, otherProvider.CallCount);
        var messages = await store.GetMessagesAsync(ownedTurn.ConversationId, CancellationToken.None);
        Assert.Equal(2, messages.Count);
        var metadata = await store.GetMetadataAsync(ownedTurn.ConversationId, CancellationToken.None);
        Assert.Equal("owner-principal", metadata?.OwnerPrincipalId);
    }

    [Fact]
    public async Task SerializesTurnsForTheSameConversation()
    {
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(store: store);
        var firstProvider = new GateProvider("First answer");
        var secondProvider = new ScriptedLanguageProvider(
        [
            request =>
            {
                Assert.Collection(
                    request.Messages,
                    message => Assert.Equal(ConversationRole.System, message.Role),
                    message => Assert.Equal("First message", message.Content),
                    message => Assert.Equal("First answer", message.Content),
                    message => Assert.Equal("Second message", message.Content));
                return LanguageProviderResponse.Final("Second answer");
            },
        ]);
        var conversationId = Guid.NewGuid();

        var firstTurn = sut.ProcessAsync(
            new ConversationTurnRequest("First message", conversationId),
            firstProvider,
            CancellationToken.None);
        await firstProvider.WaitUntilCalledAsync();

        var secondTurn = sut.ProcessAsync(
            new ConversationTurnRequest("Second message", conversationId),
            secondProvider,
            CancellationToken.None);

        Assert.Equal(0, secondProvider.CallCount);
        firstProvider.Complete();

        Assert.True((await firstTurn).IsSuccess);
        Assert.True((await secondTurn).IsSuccess);
        Assert.Equal(1, secondProvider.CallCount);
    }

    [Fact]
    public async Task CancellingATurnWaitingForTheConversationLockDoesNotAppendOrCallProvider()
    {
        var store = new InMemoryConversationStore();
        var sut = CreateOrchestrator(store: store);
        var firstProvider = new GateProvider("First answer");
        var secondProvider = new ScriptedLanguageProvider([]);
        var conversationId = Guid.NewGuid();

        var firstTurn = sut.ProcessAsync(
            new ConversationTurnRequest("First message", conversationId),
            firstProvider,
            CancellationToken.None);
        await firstProvider.WaitUntilCalledAsync();

        using var cancellation = new CancellationTokenSource();
        var cancelledTurn = sut.ProcessAsync(
            new ConversationTurnRequest("Second message", conversationId),
            secondProvider,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTurn);
        firstProvider.Complete();
        Assert.True((await firstTurn).IsSuccess);
        Assert.Equal(0, secondProvider.CallCount);

        var messages = await store.GetMessagesAsync(conversationId, CancellationToken.None);
        Assert.Collection(
            messages,
            message => Assert.Equal("First message", message.Content),
            message => Assert.Equal("First answer", message.Content));
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
        var trace = Assert.Single(result.Tools, trace => trace.ToolCallId == "call-time");
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
    public async Task UsesSafeClientMessageAndExcludesToolContentFromAudit()
    {
        var auditSink = new InMemoryToolAuditSink();
        var tool = new DelegateTool(
            "sensitive_failure",
            requiresConfirmation: false,
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Failure(
                "tool_failed",
                "Sensitive provider result and secret argument",
                "The operation could not be completed.")));
        var provider = ProviderRequesting(new ToolCall(
            "sensitive-call",
            "sensitive_failure",
            JsonSerializer.SerializeToElement(new { secret = "never-audit" })));
        var sut = CreateOrchestrator(tools: [tool], auditSink: auditSink);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Run it"),
            provider,
            CancellationToken.None);

        Assert.Equal("tool_failed", result.Error?.Code);
        Assert.Equal("The operation could not be completed.", result.Error?.Message);
        var events = auditSink.Events;
        Assert.Collection(
            events,
            auditEvent => Assert.Equal(ToolAuditEventType.Requested, auditEvent.Type),
            auditEvent => Assert.Equal(ToolAuditEventType.ExecutionStarted, auditEvent.Type),
            auditEvent => Assert.Equal(ToolAuditEventType.ExecutionFailed, auditEvent.Type));
        var serializedEvents = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("Sensitive provider result", serializedEvents, StringComparison.Ordinal);
        Assert.DoesNotContain("never-audit", serializedEvents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditsPolicyDenialWithoutExecutingTool()
    {
        var auditSink = new InMemoryToolAuditSink();
        var tool = new DelegateTool(
            "private_tool",
            new ToolRiskProfile(
                ToolOperationImpact.ReadOnly,
                ToolDataSensitivity.Private,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: false,
                ["private.read"]),
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Success("unreachable")));
        var provider = ProviderRequesting(new ToolCall("private-call", "private_tool", EmptyArguments()));
        var sut = CreateOrchestrator(tools: [tool], auditSink: auditSink);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Read private data"),
            provider,
            CancellationToken.None);

        Assert.Equal("authentication_required", result.Error?.Code);
        Assert.Collection(
            auditSink.Events,
            auditEvent => Assert.Equal(ToolAuditEventType.Requested, auditEvent.Type),
            auditEvent =>
            {
                Assert.Equal(ToolAuditEventType.PolicyDenied, auditEvent.Type);
                Assert.Equal("authentication_required", auditEvent.OutcomeCode);
            });
    }

    [Fact]
    public async Task AuditsApprovedConfirmationBeforeExecutingTool()
    {
        var auditSink = new InMemoryToolAuditSink();
        var tool = new DelegateTool(
            "change_tool",
            requiresConfirmation: true,
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Success("changed")));
        var call = new ToolCall("change-call", "change_tool", EmptyArguments());
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Done")),
        ]);
        var sut = CreateOrchestrator(tools: [tool], auditSink: auditSink);

        var pending = await sut.ProcessAsync(
            new ConversationTurnRequest("Change it"),
            provider,
            CancellationToken.None);
        var result = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            approved: true,
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            auditSink.Events,
            auditEvent => Assert.Equal(ToolAuditEventType.Requested, auditEvent.Type),
            auditEvent => Assert.Equal(ToolAuditEventType.ConfirmationRequested, auditEvent.Type),
            auditEvent => Assert.Equal(ToolAuditEventType.ConfirmationApproved, auditEvent.Type),
            auditEvent => Assert.Equal(ToolAuditEventType.ExecutionStarted, auditEvent.Type),
            auditEvent => Assert.Equal(ToolAuditEventType.ExecutionSucceeded, auditEvent.Type));
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

    [Fact]
    public async Task DoesNotExposeSensitiveToolToAnonymousProvider()
    {
        var sensitiveTool = new DelegateTool(
            "read_sensitive",
            new ToolRiskProfile(
                ToolOperationImpact.ReadOnly,
                ToolDataSensitivity.Sensitive,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: false,
                []),
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Success("secret")));
        var provider = new ScriptedLanguageProvider([
            request =>
            {
                Assert.DoesNotContain(request.AvailableTools, tool => tool.Metadata.Name == "read_sensitive");
                return LanguageProviderResponse.Final("Safe answer");
            },
        ]);
        var sut = CreateOrchestrator(tools: [sensitiveTool]);

        var result = await sut.ProcessAsync(new ConversationTurnRequest("Read data"), provider, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DeniesSensitiveToolRequestedByProviderWithoutExecutingIt()
    {
        var executions = 0;
        var sensitiveTool = new DelegateTool(
            "read_sensitive",
            new ToolRiskProfile(
                ToolOperationImpact.ReadOnly,
                ToolDataSensitivity.Sensitive,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: false,
                []),
            (_, _) =>
            {
                executions++;
                return ValueTask.FromResult(ToolExecutionResult.Success("secret"));
            });
        var sut = CreateOrchestrator(tools: [sensitiveTool]);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Read data"),
            ProviderRequesting(new ToolCall("sensitive-1", "read_sensitive", EmptyArguments())),
            CancellationToken.None);

        Assert.Equal("authentication_required", result.Error?.Code);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task RechecksPolicyImmediatelyBeforeExecutingTool()
    {
        var executions = 0;
        var privateTool = new DelegateTool(
            "read_private",
            new ToolRiskProfile(
                ToolOperationImpact.ReadOnly,
                ToolDataSensitivity.Private,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: false,
                []),
            (_, _) =>
            {
                executions++;
                return ValueTask.FromResult(ToolExecutionResult.Success("private data"));
            });
        var authenticated = new ToolPolicyContext(
            "test-principal",
            new HashSet<string>(StringComparer.Ordinal));
        var contextAccessor = new SequenceToolPolicyContextAccessor(
            authenticated,
            ToolPolicyContext.Anonymous);
        var sut = CreateOrchestrator(
            tools: [privateTool],
            toolPolicyContextAccessor: contextAccessor);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Read data"),
            ProviderRequesting(new ToolCall("private-1", "read_private", EmptyArguments())),
            CancellationToken.None);

        Assert.Equal("authentication_required", result.Error?.Code);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task RequiresConfirmationForSignificantCostReadOnlyTool()
    {
        var costlyTool = new DelegateTool(
            "costly_read",
            new ToolRiskProfile(
                ToolOperationImpact.ReadOnly,
                ToolDataSensitivity.Public,
                ToolExposure.Local,
                ToolCost.Significant,
                RequiresConfirmation: false,
                []),
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Success("done")));
        var sut = CreateOrchestrator(tools: [costlyTool]);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Run costly read"),
            ProviderRequesting(new ToolCall("costly-1", "costly_read", EmptyArguments())),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Confirmation);
    }

    [Fact]
    public async Task DoesNotAllowAnotherPrincipalToResolvePendingConfirmation()
    {
        var executions = 0;
        var tool = new DelegateTool("change_state", true, (_, _) =>
        {
            executions++;
            return ValueTask.FromResult(ToolExecutionResult.Success("changed"));
        });
        var firstPrincipal = new ToolPolicyContext(
            "first-principal",
            new HashSet<string>(StringComparer.Ordinal));
        var otherPrincipal = new ToolPolicyContext(
            "other-principal",
            new HashSet<string>(StringComparer.Ordinal));
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(
                new ToolCall("change-1", "change_state", EmptyArguments()))),
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Done")),
        ]);
        var sut = CreateOrchestrator(
            tools: [tool],
            toolPolicyContextAccessor: new SequenceToolPolicyContextAccessor(
                firstPrincipal,
                otherPrincipal,
                firstPrincipal,
                firstPrincipal));

        var pending = await sut.ProcessAsync(new ConversationTurnRequest("Change"), provider, CancellationToken.None);
        var denied = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            true,
            provider,
            CancellationToken.None);

        Assert.Equal("conversation_not_found", denied.Error?.Code);
        Assert.Equal(0, executions);

        var approved = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            true,
            provider,
            CancellationToken.None);

        Assert.True(approved.IsSuccess);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task RejectionDoesNotExecuteToolAndReturnsToolResultToProvider()
    {
        var executions = 0;
        var tool = new DelegateTool("change_state", true, (_, _) =>
        {
            executions++;
            return ValueTask.FromResult(ToolExecutionResult.Success("changed"));
        });
        var call = new ToolCall("change-1", "change_state", EmptyArguments());
        var provider = new ScriptedLanguageProvider([
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
            request =>
            {
                var toolResult = request.Messages.Last(message => message.ToolResult is not null).ToolResult!;
                Assert.True(toolResult.IsError);
                Assert.Equal("The user rejected this tool call.", toolResult.Content);
                return LanguageProviderResponse.Final("No change was made.");
            },
        ]);
        var sut = CreateOrchestrator(tools: [tool]);

        var pending = await sut.ProcessAsync(new ConversationTurnRequest("Change"), provider, CancellationToken.None);
        var result = await sut.ResolveConfirmationAsync(pending.ConversationId, pending.Confirmation!.ConfirmationId, false, provider, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("No change was made.", result.Content);
        Assert.Equal(0, executions);
        Assert.Equal("tool_confirmation_rejected", Assert.Single(result.Tools).ErrorCode);
    }

    [Fact]
    public async Task ExpiredConfirmationDoesNotExecuteTool()
    {
        var executions = 0;
        var timeProvider = new ManualTimeProvider(FixedUtcNow);
        var tool = new DelegateTool("change_state", true, (_, _) =>
        {
            executions++;
            return ValueTask.FromResult(ToolExecutionResult.Success("changed"));
        });
        var provider = ProviderRequesting(new ToolCall("change-1", "change_state", EmptyArguments()));
        var sut = CreateOrchestrator(
            tools: [tool],
            timeProvider: timeProvider,
            options: new OrchestrationOptions { ConfirmationTimeout = TimeSpan.FromSeconds(1) });

        var pending = await sut.ProcessAsync(new ConversationTurnRequest("Change"), provider, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await sut.ResolveConfirmationAsync(pending.ConversationId, pending.Confirmation!.ConfirmationId, true, provider, CancellationToken.None);

        Assert.Equal("confirmation_expired", result.Error?.Code);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task RejectsResolutionFromAnotherProviderWithoutConsumingConfirmation()
    {
        var tool = new DelegateTool(
            "change_state",
            true,
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Success("changed")));
        var provider = ProviderRequesting(new ToolCall("change-1", "change_state", EmptyArguments()));
        var sut = CreateOrchestrator(tools: [tool]);

        var pending = await sut.ProcessAsync(new ConversationTurnRequest("Change"), provider, CancellationToken.None);
        var result = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            true,
            new NamedProvider("other-provider"),
            CancellationToken.None);

        Assert.Equal("confirmation_provider_mismatch", result.Error?.Code);

        var stillPending = await sut.ProcessAsync(
            new ConversationTurnRequest("Another message", pending.ConversationId),
            provider,
            CancellationToken.None);
        Assert.Equal("confirmation_pending", stillPending.Error?.Code);
    }

    [Fact]
    public async Task ResolvingTheSameConfirmationTwiceDoesNotExecuteToolTwice()
    {
        var executions = 0;
        var tool = new DelegateTool("change_state", true, (_, _) =>
        {
            executions++;
            return ValueTask.FromResult(ToolExecutionResult.Success("changed"));
        });
        var call = new ToolCall("change-1", "change_state", EmptyArguments());
        var provider = new ScriptedLanguageProvider([
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Done")),
        ]);
        var sut = CreateOrchestrator(tools: [tool]);

        var pending = await sut.ProcessAsync(new ConversationTurnRequest("Change"), provider, CancellationToken.None);
        var confirmationId = pending.Confirmation!.ConfirmationId;
        var first = await sut.ResolveConfirmationAsync(pending.ConversationId, confirmationId, true, provider, CancellationToken.None);
        var second = await sut.ResolveConfirmationAsync(pending.ConversationId, confirmationId, true, provider, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal("confirmation_not_found", second.Error?.Code);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task SuppliesAuthoritativeTimeBeforeAProviderRespondsDirectly()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 30, 12, 4, 0, TimeSpan.Zero));
        var provider = new ScriptedLanguageProvider(
        [
            request =>
            {
                var timeContext = Assert.Single(
                    request.Messages,
                    message => message.Role == ConversationRole.System &&
                               message.Content!.Contains("Authoritative current time", StringComparison.Ordinal));
                Assert.Contains("2026-08-30T12:04:00.0000000+00:00", timeContext.Content, StringComparison.Ordinal);
                return LanguageProviderResponse.Final("The authoritative time is 12:04 UTC.");
            },
        ]);
        var sut = CreateOrchestrator(timeProvider: clock);

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("Hola Jarvis. ¿Puedes darme la hora local?"),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var trace = Assert.Single(result.Tools);
        Assert.Equal("authoritative-current-time", trace.ToolCallId);
        Assert.Equal(CurrentTimeTool.ToolName, trace.ToolName);
    }

    [Fact]
    public async Task RefreshesAuthoritativeTimeAfterAnApprovedConfirmation()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 30, 12, 4, 0, TimeSpan.Zero));
        var tool = new DelegateTool(
            "change_state",
            true,
            static (_, _) => ValueTask.FromResult(ToolExecutionResult.Success("changed")));
        var call = new ToolCall("change-1", "change_state", EmptyArguments());
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(call)),
            request =>
            {
                var timeContext = Assert.Single(
                    request.Messages,
                    message => message.Role == ConversationRole.System &&
                               message.Content!.Contains(
                                   "Authoritative current time",
                                   StringComparison.Ordinal));
                Assert.Contains(
                    "2026-08-30T12:05:00.0000000+00:00",
                    timeContext.Content,
                    StringComparison.Ordinal);
                return LanguageProviderResponse.Final("Done.");
            },
        ]);
        var sut = CreateOrchestrator(tools: [tool], timeProvider: clock);

        var pending = await sut.ProcessAsync(
            new ConversationTurnRequest("Change this and tell me the current time."),
            provider,
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await sut.ResolveConfirmationAsync(
            pending.ConversationId,
            pending.Confirmation!.ConfirmationId,
            true,
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(
            result.Tools,
            trace => trace.ToolCallId == "authoritative-current-time" && trace.Succeeded);
    }

    [Theory]
    [InlineData("Explícame qué es UTC")]
    [InlineData("Escribe literalmente get_current_time")]
    [InlineData("Convierte las 15:00 de Madrid a Nueva York")]
    public async Task DoesNotResolveCurrentTimeForExcludedRequests(string message)
    {
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Explanation.")),
        ]);
        var sut = CreateOrchestrator();

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest(message),
            provider,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task ReturnsAControlledErrorForAnInvalidAuthorizedHouseholdTimeZone()
    {
        var householdProfile = new HouseholdProfile(
            "Unknown",
            "Invalid/TimeZone",
            FixedUtcNow,
            "test");
        var policyContext = new ToolPolicyContext(
            "owner",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "household.profile.read",
            });
        var provider = new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.Final("Unreachable.")),
        ]);
        var sut = CreateOrchestrator(
            toolPolicyContextAccessor: new MutableToolPolicyContextAccessor(policyContext),
            householdProfiles: new StaticHouseholdProfileStore(householdProfile));

        var result = await sut.ProcessAsync(
            new ConversationTurnRequest("¿Qué hora es?"),
            provider,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_household_time_zone", result.Error?.Code);
        Assert.Equal(0, provider.CallCount);
        var trace = Assert.Single(result.Tools);
        Assert.Equal("authoritative-current-time", trace.ToolCallId);
        Assert.False(trace.Succeeded);
    }

    private static ConversationOrchestrator CreateOrchestrator(
        IEnumerable<ITool>? tools = null,
        ManualTimeProvider? timeProvider = null,
        InMemoryConversationStore? store = null,
        OrchestrationOptions? options = null,
        IToolPolicyContextAccessor? toolPolicyContextAccessor = null,
        IToolAuditSink? auditSink = null,
        IAssistantProfileStore? profiles = null,
        IConversationContextRetriever? conversationContextRetriever = null,
        IHouseholdProfileStore? householdProfiles = null)
    {
        timeProvider ??= new ManualTimeProvider(FixedUtcNow);
        tools ??= [new CurrentTimeTool(timeProvider)];

        return new ConversationOrchestrator(
            store ?? new InMemoryConversationStore(),
            conversationContextRetriever ?? new NullConversationContextRetriever(),
            profiles ?? new MutableAssistantProfileStore(AssistantProfile.DefaultDisplayName),
            new ToolRegistry(tools),
            new DefaultToolRiskPolicy(),
            toolPolicyContextAccessor ?? new AnonymousToolPolicyContextAccessor(),
            auditSink ?? new InMemoryToolAuditSink(),
            new InMemoryToolConfirmationStore(),
            new InMemoryConversationExecutionLock(),
            timeProvider,
            Options.Create(options ?? new OrchestrationOptions()),
            NullLogger<ConversationOrchestrator>.Instance,
            householdProfiles: householdProfiles);
    }

    private sealed class MutableAssistantProfileStore(string displayName) : IAssistantProfileStore
    {
        private AssistantProfile _profile = AssistantProfile.Create(displayName);

        public ValueTask<AssistantProfile> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profile);
        }

        public ValueTask<AssistantProfile> SetDisplayNameAsync(
            string displayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profile = AssistantProfile.Create(displayName);
            return ValueTask.FromResult(_profile);
        }
    }

    private sealed class StaticHouseholdProfileStore(HouseholdProfile profile) : IHouseholdProfileStore
    {
        public ValueTask<HouseholdProfile?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<HouseholdProfile?>(profile);
        }

        public ValueTask<HouseholdProfile> SetLocationAsync(
            string location,
            string timeZoneId,
            string source,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<HouseholdProfile>(
                new InvalidOperationException("The test store is read-only."));
    }

    private sealed class StaticConversationContextRetriever(
        ConversationRetrievedContext context) : IConversationContextRetriever
    {
        public int CallCount { get; private set; }

        public ValueTask<ConversationRetrievalResult> RetrieveAsync(
            string ownerPrincipalId,
            Guid currentConversationId,
            string message,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new ConversationRetrievalResult([context]));
        }
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

    private sealed class GateProvider(string response) : ILanguageProvider
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "gate";

        public Task WaitUntilCalledAsync() => _called.Task;

        public void Complete() => _release.TrySetResult();

        public async Task<LanguageProviderResponse> GetResponseAsync(
            LanguageProviderRequest request,
            CancellationToken cancellationToken)
        {
            _called.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return LanguageProviderResponse.Final(response);
        }
    }

    private sealed class NamedProvider(string name) : ILanguageProvider
    {
        public string Name { get; } = name;

        public Task<LanguageProviderResponse> GetResponseAsync(
            LanguageProviderRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(LanguageProviderResponse.Final("unreachable"));
    }

    private sealed class SequenceToolPolicyContextAccessor(
        params ToolPolicyContext[] contexts) : IToolPolicyContextAccessor
    {
        private int _index;

        public ToolPolicyContext GetCurrent()
        {
            var index = Math.Min(_index, contexts.Length - 1);
            _index++;
            return contexts[index];
        }
    }

    private sealed class MutableToolPolicyContextAccessor(ToolPolicyContext current) : IToolPolicyContextAccessor
    {
        public ToolPolicyContext Current { get; set; } = current;

        public ToolPolicyContext GetCurrent() => Current;
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
            : this(
                name,
                requiresConfirmation
                    ? new ToolRiskProfile(
                        ToolOperationImpact.ChangesState,
                        ToolDataSensitivity.Public,
                        ToolExposure.Local,
                        ToolCost.None,
                        RequiresConfirmation: true,
                        [])
                    : ToolRiskProfile.PublicLocalRead,
                execute)
        {
        }

        public DelegateTool(
            string name,
            ToolRiskProfile risk,
            Func<JsonElement, CancellationToken, ValueTask<ToolExecutionResult>> execute)
        {
            Definition = new ToolDefinition(
                new ToolMetadata(
                    name,
                    "Test tool",
                    risk),
                Schema);
            _execute = execute;
        }

        public ToolDefinition Definition { get; }

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) => _execute(arguments, cancellationToken);
    }
}
