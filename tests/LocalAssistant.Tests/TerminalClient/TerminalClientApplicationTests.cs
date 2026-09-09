using System.Net;
using System.Reflection;
using System.Text;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class TerminalClientApplicationTests
{
    [Fact]
    public async Task FakeConversationReusesConversationIdAndDoesNotWriteTheCredential()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First message", "Second message", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake", "--scenario=direct"]));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[2].Body.RootElement.TryGetProperty("conversationId", out _));
        Assert.Equal(
            conversationId,
            handler.Requests[3].Body.RootElement.GetProperty("conversationId").GetGuid());
        Assert.Contains("Provider: fake (scenario: direct)", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: First response", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-a", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredConversationErrorReusesConversationIdForTheNextMessage()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var sink = new RecordingTerminalClientStateSink();
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(
                HttpStatusCode.GatewayTimeout,
                FailedConversationResponseJson(conversationId, "provider_timeout")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First message", "Second message", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake", "--scenario=direct"]),
            new TestCredentialStore(),
            sink,
            spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[2].Body.RootElement.TryGetProperty("conversationId", out _));
        Assert.Equal(
            conversationId,
            handler.Requests[3].Body.RootElement.GetProperty("conversationId").GetGuid());
        Assert.Contains("Conversation error: provider_timeout", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
            snapshot.Error?.Severity == TerminalClientErrorSeverity.Recoverable &&
            snapshot.Error.Code == "provider_timeout" &&
            snapshot.ConversationId == conversationId);
        Assert.Equal(["Second response"], spokenOutput.PreparedTexts);
    }

    [Fact]
    public async Task PendingConfirmationCanBeRejected()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "accessToken": "session-token", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }
                """),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5100/"),
        };
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "reject", null],
            "credential-a");
        var application = new TerminalClientApplication(
            new PrivateApiClient(httpClient),
            console,
            TerminalClientOptions.Parse(["--provider=fake"]));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Confirmation required", console.Output, StringComparison.Ordinal);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task FinalResponseUsesSpokenOutputAfterItIsShown()
    {
        var conversationId = Guid.Parse("45701f36-0ce8-4ac3-8c09-e49fcdeed895");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Final response"], spokenOutput.PreparedTexts);
        Assert.Equal(1, spokenOutput.PlayCount);
        Assert.Contains("Assistant: Final response", console.Output, StringComparison.Ordinal);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Activity == TerminalClientActivity.PlayingVoice &&
            snapshot.SpokenOutput.Availability == SpokenOutputAvailability.Ready);
    }

    [Fact]
    public async Task RateCommandPersistsAndAppliesPreferencesBeforeTheNextResponse()
    {
        var conversationId = Guid.Parse("5341758d-0f47-44a3-a93a-dc9d224be5dc");
        var store = new TestCredentialStore();
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/rate 3", "Hello", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, store.Preferences.Rate);
        Assert.Equal(3, spokenOutput.State.Rate);
        Assert.Equal(["Final response"], spokenOutput.PreparedTexts);
    }

    [Fact]
    public async Task RepeatUsesTheLastEligibleResponseWithoutSendingAnotherHttpRequest()
    {
        var conversationId = Guid.Parse("4b6c6af2-61c0-4a0c-9c75-80f17d72a3b6");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Repeat me")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", "/repeat", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("Unknown command", console.Output, StringComparison.Ordinal);
        Assert.Equal(["Repeat me", "Repeat me"], spokenOutput.PreparedTexts);
        Assert.Equal(2, spokenOutput.PlayCount);
    }

    [Fact]
    public async Task SpokenCommandsRejectUnexpectedArgumentsAndStopExplainsWhenNoAudioIsActive()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/stop now", "/repeat later", "/stop", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: new RecordingSpokenOutputCoordinator());

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: /stop", console.Output, StringComparison.Ordinal);
        Assert.Contains("Usage: /repeat", console.Output, StringComparison.Ordinal);
        Assert.Contains("There is no spoken output to stop.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopDuringPlaybackCancelsOnlyTheLocalAudioAndKeepsTheTurnResult()
    {
        var conversationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var spokenOutput = new RecordingSpokenOutputCoordinator { BlockPlaybackUntilCancellation = true };
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Spoken response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new DeferredInputConsole();
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        console.Provide("Hello");
        console.Provide("/stop");
        console.Provide(null);
        var exitCode = await application.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, spokenOutput.StopCount);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(["Spoken response"], spokenOutput.PreparedTexts);
        Assert.DoesNotContain("UNCERTAIN", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALineTypedDuringPlaybackIsDeferredAndHandledExactlyOnceAfterwards()
    {
        var conversationId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var spokenOutput = new RecordingSpokenOutputCoordinator { BlockPlaybackUntilCancellation = true };
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Spoken response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new DeferredInputConsole();
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        console.Provide("Hello");
        console.Provide("/mute");
        console.Provide(null);
        var runTask = application.RunAsync(CancellationToken.None);
        await spokenOutput.WaitForPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(5));
        spokenOutput.CompletePlayback();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.True(store.Preferences.IsMuted);
        Assert.True(spokenOutput.RequestedPreferences.IsMuted);
        Assert.Single(spokenOutput.PreparedTexts);
        Assert.Single(store.SavedPreferences);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, console.ReadLineCallCount);
    }

    [Fact]
    public async Task WhenPlaybackFinishesFirstThePendingReadIsReusedNotReissued()
    {
        var conversationId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new DeferredInputConsole();
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        console.Provide("Hello");
        console.Provide("world");
        console.Provide(null);
        var exitCode = await application.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(["First", "Second"], spokenOutput.PreparedTexts);
        Assert.Equal(3, console.ReadLineCallCount);
    }

    [Fact]
    public async Task EndOfInputDuringPlaybackStopsTheAudioAndClosesCleanly()
    {
        var conversationId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var spokenOutput = new RecordingSpokenOutputCoordinator { BlockPlaybackUntilCancellation = true };
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Spoken response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new DeferredInputConsole();
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        console.Provide("Hello");
        console.Provide(null);
        var exitCode = await application.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, spokenOutput.StopCount);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidSpokenPreferenceValuesShowSafeUsageAndChangeNothing()
    {
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/rate 99", "/rate abc", "/volume -1", "/voice Ghost Voice", null],
            "credential-a");
        var store = new TestCredentialStore();
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Rate must be between -10 and 10.", console.Output, StringComparison.Ordinal);
        Assert.Contains("Usage: /rate <value>", console.Output, StringComparison.Ordinal);
        Assert.Contains("Volume must be between 0 and 100.", console.Output, StringComparison.Ordinal);
        Assert.Contains("The requested voice is not available.", console.Output, StringComparison.Ordinal);
        Assert.Equal(SpokenOutputPreferences.Default, spokenOutput.RequestedPreferences);
        Assert.Equal(SpokenOutputPreferences.Default, store.Preferences);
    }

    [Fact]
    public async Task ValidVoiceAndVolumeCommandsPersistSelectAndPublishTheSnapshot()
    {
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        spokenOutput.Voices.Add(new SpokenOutputVoice("Aria"));
        spokenOutput.Voices.Add(new SpokenOutputVoice("Microsoft Elvira"));
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/voice Microsoft Elvira", "/volume 55", "/voice", null],
            "credential-a");
        var store = new TestCredentialStore();
        var application = CreateApplication(httpClient, console, store, stateSink: sink, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Microsoft Elvira", store.Preferences.VoiceId);
        Assert.Equal(55, store.Preferences.Volume);
        Assert.Equal("Microsoft Elvira", spokenOutput.RequestedPreferences.VoiceId);
        Assert.Equal(55, spokenOutput.RequestedPreferences.Volume);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.SpokenOutput.VoiceId == "Microsoft Elvira" && snapshot.SpokenOutput.Volume == 55);
        Assert.Contains("Aria", console.Output, StringComparison.Ordinal);
        Assert.Contains("Requested voice: Microsoft Elvira", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatKeepsItsTextAcrossNewAndOtherNonAudioCommands()
    {
        var conversationId = Guid.Parse("bcbcbcbc-bcbc-4cbc-8cbc-bcbcbcbcbcbc");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Keep me")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", "/help", "/new", "/repeat", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Keep me", "Keep me"], spokenOutput.PreparedTexts);
        Assert.DoesNotContain("There is no response available to repeat.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyContentResponseIsNeitherSpokenNorRetained()
    {
        var conversationId = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", "/repeat", null], "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(spokenOutput.PreparedTexts);
        Assert.Contains("There is no response available to repeat.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVoiceFallbackWarningIsPrintedOnlyOncePerSession()
    {
        var conversationId = Guid.Parse("abababab-abab-4bab-8bab-abababababab");
        var spokenOutput = new RecordingSpokenOutputCoordinator { UsedVoiceFallback = true };
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "one", "two", null], "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, spokenOutput.PreparedTexts.Count);
        Assert.Equal(1, CountOccurrences(console.Output, "speech_voice_unavailable"));
    }

    private static int CountOccurrences(string text, string value) => text.Split(value).Length - 1;

    [Fact]
    public async Task TheTranscriptKeepsEmojiButTheSpokenTextDropsThem()
    {
        var conversationId = Guid.Parse("cdcdcdcd-cdcd-4dcd-8dcd-cdcdcdcdcdcd");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Todo listo 😊 y correcto")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", null], "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Todo listo y correcto"], spokenOutput.PreparedTexts);
        Assert.Contains("😊", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatWithNoRetainedResponseIsANormalResult()
    {
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "/repeat", null], "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("There is no response available to repeat.", console.Output, StringComparison.Ordinal);
        Assert.Empty(spokenOutput.PreparedTexts);
    }

    [Fact]
    public async Task AConfirmationOrErrorResponseIsNeitherSpokenNorRetainedForRepeat()
    {
        var conversationId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, FailedConversationResponseJson(conversationId, "tool_rejected")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "reject", "/repeat", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(spokenOutput.PreparedTexts);
        Assert.Contains("There is no response available to repeat.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynthesisFailureIsShownButKeepsTheConversationAndAllowsTheNextTurn()
    {
        var conversationId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        var spokenOutput = new RecordingSpokenOutputCoordinator
        {
            PreparationKind = SpokenOutputPreparationKind.SynthesisFailed,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", "Again", null], "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("speech_synthesis_failed", console.Output, StringComparison.Ordinal);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.ConversationId == conversationId && snapshot.Provider == "fake");
        Assert.DoesNotContain(sink.Snapshots, snapshot => snapshot.Error?.IsUncertain == true);
    }

    [Fact]
    public async Task APreferenceThatCannotBePersistedKeepsTheEffectiveValueAndReportsASafeError()
    {
        var conversationId = Guid.Parse("77777777-7777-4777-8777-777777777777");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "/rate 4", "Hello", null], "credential-a");
        var store = new TestCredentialStore { SavePreferencesResult = false };
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("speech_preferences_not_saved", console.Output, StringComparison.Ordinal);
        Assert.Equal(0, spokenOutput.RequestedPreferences.Rate);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(["Final response"], spokenOutput.PreparedTexts);
    }

    [Fact]
    public async Task MuteSkipsTheNextSynthesisAndUnmuteReenablesItWithoutReplaying()
    {
        var conversationId = Guid.Parse("88888888-8888-4888-8888-888888888888");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Muted turn")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Audible turn")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/mute", "one", "/unmute", "two", null],
            "credential-a");
        var store = new TestCredentialStore();
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Audible turn"], spokenOutput.PreparedTexts);
        Assert.Equal(1, spokenOutput.PlayCount);
        Assert.Equal([true, false], store.SavedPreferences.Select(preference => preference.IsMuted));
        Assert.False(store.Preferences.IsMuted);
    }

    [Fact]
    public async Task ProductionCompositionKeepsSpokenOutputUnavailable()
    {
        var conversationId = Guid.Parse("8856053c-10a3-47bc-a689-3a7b070342f7");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.All(sink.Snapshots, snapshot =>
            Assert.Equal(SpokenOutputAvailability.Unavailable, snapshot.SpokenOutput.Availability));
        Assert.DoesNotContain(sink.Snapshots, snapshot =>
            snapshot.Activity == TerminalClientActivity.PlayingVoice);
    }

    [Fact]
    public async Task FinalResponseAfterConfirmationUsesSpokenOutputButThePendingResponseDoesNot()
    {
        var conversationId = Guid.Parse("dc546f75-73ed-4e3a-8423-06ec6e68e2ae");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "reject", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["Reminder rejected"], spokenOutput.PreparedTexts);
        Assert.Equal(1, spokenOutput.PlayCount);
    }

    [Fact]
    public async Task EmptyFinalResponsesAndOperationalCommandsAreNotSentToSpokenOutput()
    {
        var conversationId = Guid.Parse("80147a44-9ecf-42e4-966d-cbab026ceeb2");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, string.Empty)),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/help", "Hello", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(spokenOutput.PreparedTexts);
        Assert.Equal(0, spokenOutput.PlayCount);
    }

    [Fact]
    public async Task SpokenOutputFailurePreservesTheTextAndAllowsTheNextTurn()
    {
        var firstConversationId = Guid.Parse("9d8c2574-8345-45ec-9f9f-7b8e430c2e2c");
        var spokenOutput = new RecordingSpokenOutputCoordinator
        {
            PlaybackResult = SpokenOutputPlaybackResult.PlaybackFailed,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(firstConversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(firstConversationId, "Second response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First", "Second", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Assistant: First response", console.Output, StringComparison.Ordinal);
        Assert.Contains("Error (speech_playback_failed)", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
        Assert.Equal(2, spokenOutput.PlayCount);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Code == "speech_playback_failed" &&
            !snapshot.Error.IsUncertain);
    }

    [Fact]
    public async Task SynthesisFailureIsRecoverableAndDoesNotPreventTheNextTurn()
    {
        var conversationId = Guid.Parse("c4b1da59-b8a8-4f7d-86af-5656a1d1fce6");
        var spokenOutput = new RecordingSpokenOutputCoordinator
        {
            PreparationKind = SpokenOutputPreparationKind.SynthesisFailed,
        };
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First", "Second", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["First response", "Second response"], spokenOutput.PreparedTexts);
        Assert.Equal(0, spokenOutput.PlayCount);
        Assert.Contains("Error (speech_synthesis_failed)", console.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant: Second response", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringSpokenPlaybackIsKnownAndClosesTheApplication()
    {
        var conversationId = Guid.Parse("9d15d10b-6cb4-477c-806a-53b2e1676080");
        var spokenOutput = new RecordingSpokenOutputCoordinator
        {
            BlockPlaybackUntilCancellation = true,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", null],
            "credential-a");
        using var cancellationSource = new CancellationTokenSource();
        var application = CreateApplication(httpClient, console, stateSink: sink, spokenOutput: spokenOutput);

        var runTask = application.RunAsync(cancellationSource.Token);
        await spokenOutput.WaitForPlaybackAsync();
        cancellationSource.Cancel();

        Assert.Equal(2, await runTask);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Operation == "speech_output" &&
            !snapshot.Error.IsUncertain);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task CancellationDuringSpokenSynthesisIsKnownAndClosesTheApplication()
    {
        var conversationId = Guid.Parse("cb0f5b83-c0c6-4139-90f1-9f68198ce69c");
        var spokenOutput = new RecordingSpokenOutputCoordinator
        {
            BlockPreparationUntilCancellation = true,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", null],
            "credential-a");
        using var cancellationSource = new CancellationTokenSource();
        var application = CreateApplication(httpClient, console, stateSink: sink, spokenOutput: spokenOutput);

        var runTask = application.RunAsync(cancellationSource.Token);
        await spokenOutput.WaitForPreparationAsync();
        cancellationSource.Cancel();

        Assert.Equal(2, await runTask);
        Assert.DoesNotContain(sink.Snapshots, snapshot =>
            snapshot.Activity == TerminalClientActivity.PlayingVoice);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Operation == "speech_output" &&
            !snapshot.Error.IsUncertain);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task SpokenArtifactCleanupFailureIsRecoverableAndDoesNotCloseTheClient()
    {
        var conversationId = Guid.Parse("8a634eb9-df75-43d5-89f1-7cb27ee02c0a");
        var spokenOutput = new RecordingSpokenOutputCoordinator
        {
            ThrowOnDispose = true,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Final response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Assistant: Final response", console.Output, StringComparison.Ordinal);
        Assert.Contains("Error (speech_playback_failed)", console.Output, StringComparison.Ordinal);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Code == "speech_playback_failed" &&
            !snapshot.Error.IsUncertain);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task ConfirmationRequiresAnExplicitDecision()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "typo", "reject", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.False(handler.Requests[3].Body.RootElement.GetProperty("approved").GetBoolean());
        Assert.Contains("Type approve, reject, or cancel.", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelResolvesThePendingConfirmationAsARejection()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "cancel", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("tool-confirmations", handler.Requests[3].Path, StringComparison.Ordinal);
        Assert.False(handler.Requests[3].Body.RootElement.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task StoredCredentialRejectedByTheServerOffersRecoveryAndPersistsTheReplacement()
    {
        var store = new TestCredentialStore(new PrivateClientCredential("stored-client", "stored-credential"));
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("replacement-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", null], "replacement-credential");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("stored-client", handler.Requests[1].Body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("client-a", handler.Requests[2].Body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal(new PrivateClientCredential("client-a", "replacement-credential"), store.SavedCredential);
        Assert.Contains("Recover with pairing or a manual credential", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingIsPersistedOnlyAfterTheNewCredentialOpensASession()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "paired-client", "displayName": "Desktop", "credential": "paired-credential" }
                """),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([string.Empty, "Desktop", null], "pairing-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new PrivateClientCredential("paired-client", "paired-credential"), store.SavedCredential);
        Assert.Equal("pairing-challenge", handler.Requests[1].Body.RootElement.GetProperty("challenge").GetString());
    }

    [Fact]
    public async Task PairingCredentialIsNotPersistedWhenItCannotOpenASession()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "paired-client", "displayName": "Desktop", "credential": "paired-credential" }
                """),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([string.Empty, "Desktop"], "pairing-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(store.SavedCredential);
        Assert.DoesNotContain("pairing-challenge", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("paired-credential", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionRenewalRetriesAMessageExactlyOnce()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Recovered response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[2].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task SessionRenewalDoesNotLoopWhenTheRetriedMessageIsAlsoRejected()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Contains("Error (authentication_failed)", console.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/provider ollama")]
    [InlineData("/exit")]
    public async Task CommandsCompleteAnActiveConversationWithNoContentResponse(string command)
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", command, null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal($"/api/conversations/{conversationId}/completion", handler.Requests[3].Path);
    }

    [Fact]
    public async Task CompletionRenewsTheSessionOnceBeforeRetrying()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Hello", "/new", null], "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
    }

    [Fact]
    public async Task NewCommandClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["Hello", "/new", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(store.Credential);
        Assert.Null(store.Credential.LastConversationId);
    }

    [Fact]
    public async Task ResumeHistoryNotFoundClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "conversationId": "{{conversationId}}",
                  "title": "Previous conversation",
                  "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
                  "indexingRequestedAtUtc": null
                }
                """),
            _ => JsonResponse(HttpStatusCode.NotFound, string.Empty),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["R", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(store.Credential);
        Assert.Null(store.Credential.LastConversationId);
    }

    [Fact]
    public async Task ResumeLoadsAndDisplaysThePublicHistory()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "items": [
                    { "role": "user", "content": "Previous question" },
                    { "role": "assistant", "content": "Previous answer" }
                  ],
                  "nextCursor": null
                }
                """),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["R", null]);
        var application = CreateApplication(httpClient, console, store, spokenOutput: spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
        Assert.Contains("user: Previous question", console.Output, StringComparison.Ordinal);
        Assert.Contains("assistant: Previous answer", console.Output, StringComparison.Ordinal);
        Assert.Empty(spokenOutput.PreparedTexts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ResumeValidationFailuresOtherThanNotFoundPreserveTheLastConversationId(
        HttpStatusCode statusCode)
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(statusCode, string.Empty),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task ProviderCommandClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["Hello", "/provider ollama", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task ConversationSelectorRetainsTheProviderSelectedDuringTheSession()
    {
        var conversationId = Guid.Parse("db7da1b6-cdc4-4952-8cc9-92c52cf8a51e");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => JsonResponse(HttpStatusCode.OK, """{ "items": [], "nextCursor": null }"""),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Message", "/provider ollama", "/conversations", "C", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Activity == TerminalClientActivity.SelectingConversation &&
            snapshot.Provider == "ollama");
        Assert.Equal("ollama", sink.Snapshots[^3].Provider);
    }

    [Fact]
    public async Task ConfirmationPublishesSendingAwaitingAndResolvingActivitiesInOrder()
    {
        var conversationId = Guid.Parse("326627e2-cc50-423c-8945-6e9f0fce1099");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder rejected")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "reject", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
        [
            TerminalClientActivity.SendingTurn,
            TerminalClientActivity.SendingTurn,
            TerminalClientActivity.AwaitingConfirmation,
            TerminalClientActivity.ResolvingConfirmation,
        ],
        sink.Snapshots
            .Where(snapshot => snapshot.Activity is TerminalClientActivity.SendingTurn or
                TerminalClientActivity.AwaitingConfirmation or
                TerminalClientActivity.ResolvingConfirmation)
            .Select(snapshot => snapshot.Activity));
    }

    [Fact]
    public async Task NewChoiceAtStartupClearsThePersistedLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["N", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task SelectingAnotherConversationCompletesTheCurrentConversationBeforeLoadingHistory()
    {
        var currentConversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var selectedConversationId = Guid.Parse("4b384d77-2681-4d55-8d18-cb27f74cdd1b");
        var sink = new RecordingTerminalClientStateSink();
        var spokenOutput = new RecordingSpokenOutputCoordinator();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(currentConversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "items": [
                    {
                      "conversationId": "{{selectedConversationId}}",
                      "title": "Selected conversation",
                      "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
                      "indexingRequestedAtUtc": null
                    }
                  ],
                  "nextCursor": null
                }
                """),
            _ => ConversationDetailsResponse(selectedConversationId, "Selected conversation"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "items": [ { "role": "assistant", "content": "Selected history" } ],
                  "nextCursor": null
                }
                """),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["Hello", "/conversations", "1", null]);
        var application = CreateApplication(httpClient, console, store, sink, spokenOutput);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/conversations/{currentConversationId}/completion", handler.Requests[5].Path);
        Assert.Equal($"/api/conversations/{selectedConversationId}/history", handler.Requests[6].Path);
        Assert.Equal(selectedConversationId, store.Credential!.LastConversationId);
        Assert.Contains("assistant: Selected history", console.Output, StringComparison.Ordinal);
        var selectingIndex = sink.Snapshots.FindIndex(snapshot =>
            snapshot.Activity == TerminalClientActivity.SelectingConversation);
        var completingIndex = sink.Snapshots.FindIndex(snapshot =>
            snapshot.Activity == TerminalClientActivity.CompletingConversation);
        Assert.True(selectingIndex >= 0);
        Assert.True(completingIndex > selectingIndex);
        Assert.Equal(["First response"], spokenOutput.PreparedTexts);
    }

    [Fact]
    public async Task ResumeRenewsTheBearerWhenLoadingConversationDetails()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["N", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Bearer expired-token", handler.Requests[2].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task ResumeRenewsTheBearerWhenLoadingConversationHistory()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => ConversationDetailsResponse(conversationId, "Previous conversation"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "items": [ { "role": "assistant", "content": "History" } ], "nextCursor": null }
                """),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["R", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
    }

    [Fact]
    public async Task ConversationSelectorRenewsTheBearerWhenListing()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "items": [], "nextCursor": null }"""),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["/conversations", "C", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("Bearer expired-token", handler.Requests[2].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task ConversationSelectorRequestsTheNextPageBeforeSelection()
    {
        var selectedConversationId = Guid.Parse("4b384d77-2681-4d55-8d18-cb27f74cdd1b");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "items": [],
                  "nextCursor": "opaque-next-page"
                }
                """),
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "items": [
                    {
                      "conversationId": "{{selectedConversationId}}",
                      "title": "Second page",
                      "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
                      "indexingRequestedAtUtc": null
                    }
                  ],
                  "nextCursor": null
                }
                """),
            _ => ConversationDetailsResponse(selectedConversationId, "Second page"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "items": [ { "role": "assistant", "content": "History" } ], "nextCursor": null }
                """),
        ]);
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["/conversations", "N", "1", null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(selectedConversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task InvalidResumeDetailsResponsePreservesTheLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, "{}"),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task ResumeTimeoutPreservesTheLastConversationId()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => throw new TaskCanceledException(),
        ]);
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(conversationId, store.Credential!.LastConversationId);
    }

    [Fact]
    public async Task FailedCompletionRetainsTheRenewedSessionForTheNextMessage()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "First response")),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Second response")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Hello", "/new", "Again", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(7, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[6].Authorization);
    }

    [Fact]
    public async Task ConfirmationRenewsTheSessionOnceBeforeRetrying()
    {
        var conversationId = Guid.Parse("a51b02fb-29d0-47ae-87dc-808d5ee29656");
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("expired-token"),
            _ => JsonResponse(HttpStatusCode.Accepted, ConfirmationResponseJson(conversationId)),
            _ => JsonResponse(HttpStatusCode.Unauthorized, string.Empty),
            _ => SessionResponse("renewed-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Reminder created")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Create a reminder", "approve", null],
            "credential-a");
        var application = CreateApplication(httpClient, console);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("Bearer expired-token", handler.Requests[3].Authorization);
        Assert.Equal("Bearer renewed-token", handler.Requests[5].Authorization);
    }

    [Fact]
    public async Task RotationValidatesAndPersistsTheReplacementCredential()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "client-a", "displayName": "Desktop", "credential": "replacement-credential" }
                """),
            _ => SessionResponse("replacement-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate", null],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("client-a", handler.Requests[2].Body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal(new PrivateClientCredential("client-a", "replacement-credential"), store.SavedCredential);
        Assert.DoesNotContain("credential-a", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("rotation-challenge", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-credential", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("old-token", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-token", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationForAnotherClientDoesNotReplaceTheLocalCredential()
    {
        var store = new TestCredentialStore();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "other-client", "displayName": "Desktop", "credential": "other-credential" }
                """),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate", null],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(new PrivateClientCredential("client-a", "credential-a"), store.SavedCredential);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Operation == "credential_rotation" &&
            snapshot.Error.Code == "invalid_response" &&
            snapshot.Error.IsUncertain);
    }

    [Fact]
    public async Task RotationWarnsWhenTheReplacementCredentialCannotBePersisted()
    {
        var store = new TestCredentialStore
        {
            FailAfterFirstSave = true,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "clientId": "client-a", "displayName": "Desktop", "credential": "replacement-credential" }
                """),
            _ => SessionResponse("replacement-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate", null],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("The credential was rotated but could not be stored", console.Output, StringComparison.Ordinal);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Code == "rotated_credential_not_saved" &&
            snapshot.Error.Operation == "credential_rotation" &&
            snapshot.Error.Severity == TerminalClientErrorSeverity.Blocking);
    }

    [Fact]
    public async Task UncertainCredentialRotationFailureBlocksTheClient()
    {
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("old-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin rotate"],
            "credential-a",
            "rotation-challenge");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Operation == "credential_rotation" &&
            snapshot.Error.Severity == TerminalClientErrorSeverity.Blocking &&
            snapshot.Error.IsUncertain);
    }

    [Fact]
    public async Task RevocationForTheCurrentClientDeletesOnlyItsLocalCredential()
    {
        var store = new TestCredentialStore();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "clientId": "client-a" }"""),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin revoke", "REVOKE"],
            "credential-a",
            "revocation-challenge");
        var application = CreateApplication(httpClient, console, store);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("client-a", handler.Requests[2].Body.RootElement.GetProperty("clientId").GetString());
        Assert.True(store.Deleted);
    }

    [Fact]
    public async Task RevocationForAnotherClientDoesNotDeleteTheLocalCredential()
    {
        var store = new TestCredentialStore();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "clientId": "other-client" }"""),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin revoke", "REVOKE", null],
            "credential-a",
            "revocation-challenge");
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(store.Deleted);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Operation == "credential_revocation" &&
            snapshot.Error.Code == "invalid_response" &&
            snapshot.Error.IsUncertain);
    }

    [Fact]
    public async Task FailedLocalCredentialDeletionIsPublishedAfterRevocation()
    {
        var store = new TestCredentialStore
        {
            DeleteResult = false,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, """{ "clientId": "client-a" }"""),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "/admin revoke", "REVOKE"],
            "credential-a",
            "revocation-challenge");
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Code == "revoked_credential_not_deleted" &&
            snapshot.Error.Operation == "credential_revocation" &&
            snapshot.Error.Severity == TerminalClientErrorSeverity.Blocking);
    }

    [Fact]
    public async Task FailedLastConversationPersistenceIsPublishedAsARecoverableError()
    {
        var conversationId = Guid.Parse("e485d784-6910-4dcb-993f-eef09b5ab0f1");
        var store = new TestCredentialStore
        {
            FailAfterFirstSave = true,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Message", null], "credential-a");
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.Code == "last_conversation_not_saved" &&
            snapshot.Error.Operation == "conversation_preference" &&
            snapshot.Error.Severity == TerminalClientErrorSeverity.Recoverable);
        Assert.Equal("last_conversation_not_saved", sink.Snapshots[^1].Error?.Code);
    }

    [Fact]
    public async Task FailedConversationPreferencePersistenceIsNotOverwrittenByATurnError()
    {
        var conversationId = Guid.Parse("6aef2778-c618-4afd-a1c2-8d0dd7ae5b3a");
        var store = new TestCredentialStore
        {
            FailAfterFirstSave = true,
        };
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(
                HttpStatusCode.GatewayTimeout,
                FailedConversationResponseJson(conversationId, "provider_timeout")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Message", null], "credential-a");
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("last_conversation_not_saved", sink.Snapshots[^1].Error?.Code);
        Assert.Contains("Conversation error: provider_timeout", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPublishesTheCompleteStartupAndClosingSequence()
    {
        var sink = new RecordingTerminalClientStateSink();
        var store = new TestCredentialStore(new PrivateClientCredential("client-a", "credential-a"));
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
        [
            (TerminalClientLifecycle.Disconnected, TerminalClientActivity.None),
            (TerminalClientLifecycle.Connecting, TerminalClientActivity.None),
            (TerminalClientLifecycle.Authenticating, TerminalClientActivity.None),
            (TerminalClientLifecycle.Ready, TerminalClientActivity.None),
            (TerminalClientLifecycle.Ready, TerminalClientActivity.ResumingConversation),
            (TerminalClientLifecycle.Ready, TerminalClientActivity.None),
            (TerminalClientLifecycle.Closing, TerminalClientActivity.None),
            (TerminalClientLifecycle.Closed, TerminalClientActivity.None),
        ],
        sink.Snapshots.Select(snapshot => (snapshot.Lifecycle, snapshot.Activity)));
    }

    [Fact]
    public async Task ExitCompletesBeforePublishingTheClosingSequence()
    {
        var conversationId = Guid.Parse("bc6b7aaf-3020-44c5-a3b9-47e9db32f24b");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Message", "/exit"], "credential-a");
        var application = CreateApplication(httpClient, console, new TestCredentialStore(), sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var completionIndex = sink.Snapshots.FindIndex(snapshot =>
            snapshot.Activity == TerminalClientActivity.CompletingConversation);
        Assert.True(completionIndex >= 0);
        Assert.Equal(TerminalClientLifecycle.Ready, sink.Snapshots[completionIndex + 1].Lifecycle);
        Assert.Equal(TerminalClientActivity.None, sink.Snapshots[completionIndex + 1].Activity);
        Assert.Equal(TerminalClientLifecycle.Closing, sink.Snapshots[^2].Lifecycle);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task UncertainTurnFailureIsPublishedWithoutBlockingTheClient()
    {
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", "Message", null], "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var uncertain = Assert.Single(
            sink.Snapshots,
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Error?.IsUncertain == true);
        Assert.Equal("turn", uncertain.Error!.Operation);
        Assert.Equal(TerminalClientLifecycle.Ready, uncertain.Lifecycle);
        Assert.Equal(TerminalClientActivity.None, uncertain.Activity);
    }

    [Fact]
    public async Task SuccessfulTurnClearsThePreviousRecoverableError()
    {
        var conversationId = Guid.Parse("ee13aa74-1971-4fcb-817e-96621190408e");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.Forbidden, string.Empty),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Recovered")),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "First message", "Second message", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, new TestCredentialStore(), sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var errorIndex = sink.Snapshots.FindIndex(snapshot =>
            snapshot.Error?.Severity == TerminalClientErrorSeverity.Recoverable &&
            snapshot.Error.Operation == "turn");
        Assert.True(errorIndex >= 0);
        Assert.Contains(
            sink.Snapshots.Skip(errorIndex + 1),
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Activity == TerminalClientActivity.None &&
                snapshot.Error is null &&
                snapshot.ConversationId == conversationId);
    }

    [Fact]
    public async Task ReadFailureIsRecoverableRatherThanUncertain()
    {
        var conversationId = Guid.Parse("24cc7ee6-91cf-4464-9909-e7f7b1159191");
        var sink = new RecordingTerminalClientStateSink();
        var store = new TestCredentialStore(
            new PrivateClientCredential("client-a", "credential-a", conversationId));
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([null]);
        var application = CreateApplication(httpClient, console, store, sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var error = Assert.Single(
            sink.Snapshots,
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Error?.Operation == "resume");
        Assert.Equal(TerminalClientErrorSeverity.Recoverable, error.Error!.Severity);
    }

    [Fact]
    public async Task BlockingHealthFailureStillClosesTheClient()
    {
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.ServiceUnavailable, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([]);
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        var blocked = Assert.Single(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked);
        Assert.Equal(TerminalClientErrorSeverity.Blocking, blocked.Error!.Severity);
        Assert.Equal(TerminalClientLifecycle.Closing, sink.Snapshots[^2].Lifecycle);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task CompletionFailureAfterDispatchIsPublishedAsUncertain()
    {
        var conversationId = Guid.Parse("989452a2-5cf8-4fdf-a83c-59d43bdad08f");
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
            _ => JsonResponse(HttpStatusCode.InternalServerError, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Message", "/exit", null],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var error = Assert.Single(
            sink.Snapshots,
            snapshot => snapshot.Lifecycle == TerminalClientLifecycle.Ready &&
                snapshot.Error?.Operation == "completion");
        Assert.True(error.Error!.IsUncertain);
        Assert.Equal(TerminalClientActivity.None, error.Activity);
    }

    [Fact]
    public async Task CancellationDuringCompletionPreservesTheUncertainCompletionError()
    {
        var conversationId = Guid.Parse("4f4a2a1d-3149-406e-8d20-3ff634fc39e0");
        using var cancellationSource = new CancellationTokenSource();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
            _ => JsonResponse(HttpStatusCode.OK, ConversationResponseJson(conversationId, "Answer")),
            _ =>
            {
                cancellationSource.Cancel();
                throw new OperationCanceledException(cancellationSource.Token);
            },
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(
            ["client-a", "Message", "/exit"],
            "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(cancellationSource.Token);

        Assert.Equal(2, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Error?.IsUncertain == true &&
            snapshot.Error!.Operation == "completion");
        var finalError = Assert.IsType<TerminalClientOperationError>(sink.Snapshots[^1].Error);
        Assert.True(finalError.IsUncertain);
        Assert.Equal("completion", finalError.Operation);
    }

    [Fact]
    public async Task PairingFailureIsPublishedAsABlockingPairingError()
    {
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => JsonResponse(HttpStatusCode.Forbidden, string.Empty),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([string.Empty, "Desktop"], "pairing-challenge");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, exitCode);
        var pairingError = Assert.Single(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Operation == "pairing");
        Assert.Equal(TerminalClientErrorSeverity.Blocking, pairingError.Error!.Severity);
    }

    [Fact]
    public async Task CancellationDuringPairingPreservesTheUncertainPairingError()
    {
        using var cancellationSource = new CancellationTokenSource();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ =>
            {
                cancellationSource.Cancel();
                throw new OperationCanceledException(cancellationSource.Token);
            },
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([string.Empty, "Desktop"], "pairing-challenge");
        var application = CreateApplication(httpClient, console, stateSink: sink);

        var exitCode = await application.RunAsync(cancellationSource.Token);

        Assert.Equal(1, exitCode);
        var pairingError = Assert.Single(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Operation == "pairing");
        Assert.Equal(TerminalClientErrorSeverity.Blocking, pairingError.Error!.Severity);
        Assert.True(pairingError.Error.IsUncertain);
        Assert.Equal("request_cancelled", pairingError.Error.Code);
    }

    [Fact]
    public void ApplicationThrowsWhenItRequestsAnInvalidStateTransition()
    {
        using var httpClient = CreateHttpClient(new RecordingHttpMessageHandler([]));
        using var console = new ScriptedTerminalConsole([]);
        var application = CreateApplication(httpClient, console);
        var moveTo = typeof(TerminalClientApplication).GetMethod(
            "MoveTo",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var exception = Assert.Throws<TargetInvocationException>(() =>
            moveTo!.Invoke(
                application,
                [
                    TerminalClientLifecycle.Ready,
                    TerminalClientActivity.None,
                    null,
                    null,
                    null,
                    null,
                    false,
                ]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task CapturedCancellationStillPublishesClosingAndClosed()
    {
        using var cancellationSource = new CancellationTokenSource();
        var sink = new RecordingTerminalClientStateSink();
        var handler = new RecordingHttpMessageHandler(
        [
            _ =>
            {
                cancellationSource.Cancel();
                return JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }""");
            },
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole([]);
        var application = CreateApplication(
            httpClient,
            console,
            new CancellationAwareCredentialStore(),
            sink);

        var exitCode = await application.RunAsync(cancellationSource.Token);

        Assert.Equal(2, exitCode);
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Code == "operation_cancelled");
        Assert.Contains(sink.Snapshots, snapshot =>
            snapshot.Lifecycle == TerminalClientLifecycle.Blocked &&
            snapshot.Error?.Operation == "health");
        Assert.Equal(TerminalClientLifecycle.Closing, sink.Snapshots[^2].Lifecycle);
        Assert.Equal(TerminalClientLifecycle.Closed, sink.Snapshots[^1].Lifecycle);
    }

    [Fact]
    public async Task ThrowingStateSinkDoesNotInterruptTheClient()
    {
        var handler = new RecordingHttpMessageHandler(
        [
            _ => JsonResponse(HttpStatusCode.OK, """{ "status": "healthy" }"""),
            _ => SessionResponse("session-token"),
        ]);
        using var httpClient = CreateHttpClient(handler);
        using var console = new ScriptedTerminalConsole(["client-a", null], "credential-a");
        var application = CreateApplication(httpClient, console, stateSink: new ThrowingTerminalClientStateSink());

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) => new(statusCode)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage SessionResponse(string accessToken) => JsonResponse(
        HttpStatusCode.OK,
        $$"""{ "accessToken": "{{accessToken}}", "expiresAtUtc": "2026-09-01T12:00:00+00:00" }""");

    private static HttpResponseMessage ConversationDetailsResponse(Guid conversationId, string title) => JsonResponse(
        HttpStatusCode.OK,
        $$"""
        {
          "conversationId": "{{conversationId}}",
          "title": "{{title}}",
          "lastActivityAtUtc": "2026-09-02T10:00:00+00:00",
          "indexingRequestedAtUtc": null
        }
        """);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://localhost:5100/"),
    };

    private static TerminalClientApplication CreateApplication(
        HttpClient httpClient,
        ITerminalConsole console,
        IPrivateClientCredentialStore? credentialStore = null,
        ITerminalClientStateSink? stateSink = null,
        ISpokenOutputCoordinator? spokenOutput = null)
    {
        var options = TerminalClientOptions.Parse(["--provider=fake"]);
        return stateSink is null && spokenOutput is null
            ? new TerminalClientApplication(new PrivateApiClient(httpClient), console, options, credentialStore)
            : new TerminalClientApplication(
                new PrivateApiClient(httpClient),
                console,
                options,
                credentialStore ?? new ManualPrivateClientCredentialStore(),
                stateSink ?? NullTerminalClientStateSink.Instance,
                spokenOutput);
    }

    private static string ConversationResponseJson(Guid conversationId, string content) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": "{{content}}",
          "tools": [
            {
              "toolCallId": "tool-1",
              "toolName": "current_time",
              "succeeded": true,
              "durationMilliseconds": 2,
              "errorCode": null
            }
          ],
          "iterations": 1,
          "timings": {},
          "error": null,
          "confirmation": null
        }
        """;

    private static string ConfirmationResponseJson(Guid conversationId) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": null,
          "tools": [],
          "iterations": 1,
          "timings": {},
          "error": null,
          "confirmation": {
            "confirmationId": "7a1a5909-0a04-4a30-b4d2-82b28c40f146",
            "toolCallId": "tool-1",
            "toolName": "create_reminder",
            "arguments": {},
            "expiresAtUtc": "2026-09-01T12:00:00+00:00"
          }
        }
        """;

    private static string FailedConversationResponseJson(Guid conversationId, string errorCode) => $$"""
        {
          "conversationId": "{{conversationId}}",
          "content": null,
          "tools": [],
          "iterations": 1,
          "timings": {},
          "error": {
            "code": "{{errorCode}}",
            "message": "The conversation could not be completed.",
            "toolName": null
          },
          "confirmation": null
        }
        """;
}

internal sealed class RecordingSpokenOutputCoordinator : ISpokenOutputCoordinator
{
    public TerminalClientSpokenOutputState State { get; private set; } = new(
        SpokenOutputAvailability.Ready,
        IsMuted: false);

    public SpokenOutputPreferences RequestedPreferences { get; private set; } =
        SpokenOutputPreferences.Default;

    public List<string> PreparedTexts { get; } = [];

    public int PlayCount { get; private set; }

    public SpokenOutputPreparationKind PreparationKind { get; set; } =
        SpokenOutputPreparationKind.Prepared;

    public SpokenOutputPlaybackResult PlaybackResult { get; set; } =
        SpokenOutputPlaybackResult.Completed;

    public bool BlockPlaybackUntilCancellation { get; set; }

    public bool BlockPreparationUntilCancellation { get; set; }

    public bool ThrowOnDispose { get; set; }

    public bool UsedVoiceFallback { get; set; }

    private readonly CancellationTokenSource _localStop = new();
    private readonly TaskCompletionSource _playbackRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets a blocked playback finish naturally with <see cref="PlaybackResult"/>.</summary>
    public void CompletePlayback() => _playbackRelease.TrySetResult();

    private TaskCompletionSource<bool> PlaybackStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<bool> PreparationStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public List<SpokenOutputVoice> Voices { get; } = [];

    public int VoiceEnumerationCount { get; private set; }

    public Task<IReadOnlyList<SpokenOutputVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VoiceEnumerationCount++;
        return Task.FromResult<IReadOnlyList<SpokenOutputVoice>>(Voices.ToArray());
    }

    public void UpdatePreferences(SpokenOutputPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        RequestedPreferences = preferences;
        State = new TerminalClientSpokenOutputState(
            SpokenOutputAvailability.Ready,
            preferences.IsMuted,
            preferences.VoiceId,
            preferences.Rate,
            preferences.Volume);
    }

    public int StopCount { get; private set; }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        _localStop.Cancel();
        return Task.CompletedTask;
    }

    public async Task<SpokenOutputPreparation> PrepareAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Mirror the real coordinator: an unavailable or muted output short-circuits
        // before synthesis, so the text is never "prepared".
        if (State.Availability == SpokenOutputAvailability.Unavailable)
        {
            return SpokenOutputPreparation.Unavailable;
        }

        if (State.IsMuted)
        {
            return SpokenOutputPreparation.Muted;
        }

        PreparedTexts.Add(text);
        PreparationStarted.TrySetResult(true);
        if (UsedVoiceFallback)
        {
            State = State with { WarningCode = "speech_voice_unavailable" };
        }

        if (BlockPreparationUntilCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return PreparationKind switch
        {
            SpokenOutputPreparationKind.SynthesisFailed => SpokenOutputPreparation.SynthesisFailed,
            SpokenOutputPreparationKind.Prepared => SpokenOutputPreparation.Prepared(new PreparedOutput(this)),
            _ => throw new InvalidOperationException("The spoken-output preparation kind was not recognized."),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<bool> WaitForPlaybackAsync() => PlaybackStarted.Task;

    public Task<bool> WaitForPreparationAsync() => PreparationStarted.Task;

    private sealed class PreparedOutput : IPreparedSpokenOutput
    {
        private readonly RecordingSpokenOutputCoordinator _owner;

        public PreparedOutput(RecordingSpokenOutputCoordinator owner)
        {
            _owner = owner;
        }

        public async Task<SpokenOutputPlaybackResult> PlayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _owner.PlayCount++;
            _owner.PlaybackStarted.TrySetResult(true);
            if (_owner.BlockPlaybackUntilCancellation)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _owner._localStop.Token);
                try
                {
                    await _owner._playbackRelease.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (_owner._localStop.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    // Local /stop ends the playback without cancelling the caller.
                    return SpokenOutputPlaybackResult.Cancelled;
                }
            }

            return _owner.PlaybackResult;
        }

        public ValueTask DisposeAsync()
        {
            if (_owner.ThrowOnDispose)
            {
                throw new InvalidOperationException("Cleanup failed.");
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class ScriptedTerminalConsole : ITerminalConsole, IDisposable
{
    private readonly Queue<string?> _lines;
    private readonly Queue<string> _secrets;
    private readonly StringWriter _writer = new();

    public ScriptedTerminalConsole(IEnumerable<string?> lines, params string[] secrets)
    {
        _lines = new Queue<string?>(lines);
        _secrets = new Queue<string>(secrets);
    }

    public string Output => _writer.ToString();

    public string? ReadLine() => _lines.Dequeue();

    public string ReadSecret() => _secrets.Dequeue();

    public void Write(string value) => _writer.Write(value);

    public void WriteLine(string value) => _writer.WriteLine(value);

    public void Dispose() => _writer.Dispose();
}

// A console whose message-loop reads (ReadLineAsync) stay pending until the test
// provides a value, so playback/read ordering is deterministic without Task.Delay.
internal sealed class DeferredInputConsole : ITerminalConsole, IAsyncTerminalInputConsole, IDisposable
{
    private readonly StringWriter _writer = new();
    private readonly object _sync = new();
    private readonly Queue<string?> _ready = new();
    private readonly Queue<TaskCompletionSource<string?>> _waiting = new();
    private int _readLineCallCount;

    public string Output
    {
        get { lock (_sync) { return _writer.ToString(); } }
    }

    public int ReadLineCallCount => Volatile.Read(ref _readLineCallCount);

    public void Provide(string? line)
    {
        lock (_sync)
        {
            if (_waiting.Count > 0)
            {
                _waiting.Dequeue().TrySetResult(line);
                return;
            }

            _ready.Enqueue(line);
        }
    }

    public string? ReadLine() => throw new NotSupportedException("Use ReadLineAsync.");

    public string ReadSecret() => throw new NotSupportedException("Use ReadLineAsync.");

    public void Write(string value)
    {
        lock (_sync) { _writer.Write(value); }
    }

    public void WriteLine(string value)
    {
        lock (_sync) { _writer.WriteLine(value); }
    }

    Task<string?> IAsyncTerminalInputConsole.ReadLineAsync(
        TerminalInputRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readLineCallCount);
        TaskCompletionSource<string?> completion;
        lock (_sync)
        {
            if (_ready.Count > 0)
            {
                return Task.FromResult(_ready.Dequeue());
            }

            completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(completion);
        }

        return AwaitWithCancellationAsync(completion, cancellationToken);
    }

    private static async Task<string?> AwaitWithCancellationAsync(
        TaskCompletionSource<string?> completion,
        CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    public void Dispose() => _writer.Dispose();
}

internal sealed class TestCredentialStore :
    IPrivateClientCredentialStore,
    IPrivateClientSpokenOutputPreferencesStore
{
    private int _saveCount;

    public TestCredentialStore(PrivateClientCredential? credential = null)
    {
        Credential = credential;
    }

    public PrivateClientCredential? Credential { get; private set; }

    public PrivateClientCredential? SavedCredential { get; private set; }

    public bool SaveResult { get; set; } = true;

    public bool FailAfterFirstSave { get; set; }

    public bool DeleteResult { get; set; } = true;

    public bool Deleted { get; private set; }

    public SpokenOutputPreferences Preferences { get; private set; } = SpokenOutputPreferences.Default;

    public bool SavePreferencesResult { get; set; } = true;

    public List<SpokenOutputPreferences> SavedPreferences { get; } = [];

    public Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Credential);

    public Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken)
    {
        SavedCredential = credential;
        _saveCount++;
        var saved = SaveResult && (!FailAfterFirstSave || _saveCount == 1);
        if (saved)
        {
            Credential = credential;
        }

        return Task.FromResult(saved);
    }

    public Task<bool> DeleteAsync(CancellationToken cancellationToken)
    {
        Deleted = true;
        if (DeleteResult)
        {
            Credential = null;
        }

        return Task.FromResult(DeleteResult);
    }

    public Task<SpokenOutputPreferences> LoadSpokenOutputPreferencesAsync(
        CancellationToken cancellationToken) => Task.FromResult(Preferences);

    public Task<bool> SaveSpokenOutputPreferencesAsync(
        PrivateClientCredential credential,
        SpokenOutputPreferences preferences,
        CancellationToken cancellationToken)
    {
        SavedPreferences.Add(preferences);
        if (SavePreferencesResult)
        {
            Preferences = preferences;
        }

        return Task.FromResult(SavePreferencesResult);
    }
}

internal sealed class RecordingTerminalClientStateSink : ITerminalClientStateSink
{
    public List<TerminalClientStateSnapshot> Snapshots { get; } = [];

    public void OnStateChanged(TerminalClientStateSnapshot snapshot) => Snapshots.Add(snapshot);
}

internal sealed class ThrowingTerminalClientStateSink : ITerminalClientStateSink
{
    public void OnStateChanged(TerminalClientStateSnapshot snapshot) =>
        throw new InvalidOperationException("Observer failure.");
}

internal sealed class CancellationAwareCredentialStore : IPrivateClientCredentialStore
{
    public Task<PrivateClientCredential?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PrivateClientCredential?>(null);
    }

    public Task<bool> SaveAsync(PrivateClientCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> DeleteAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
