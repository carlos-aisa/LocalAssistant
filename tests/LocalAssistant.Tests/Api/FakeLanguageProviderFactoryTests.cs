using System.Globalization;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.Api;

public sealed class FakeLanguageProviderFactoryTests
{
    [Fact]
    public async Task ReminderScenarioRequestsCreateReminderWithADueDateInTheFuture()
    {
        var factory = new FakeLanguageProviderFactory();
        Assert.True(factory.TryCreate("reminder", out var provider));

        var response = await provider!.GetResponseAsync(
            new LanguageProviderRequest(Guid.NewGuid(), [], []),
            CancellationToken.None);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(CreateReminderTool.ToolName, call.Name);

        var dueAtUtc = call.Arguments.GetProperty("dueAtUtc").GetString();
        Assert.True(
            DateTimeOffset.TryParse(
                dueAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed),
            $"dueAtUtc '{dueAtUtc}' is not a round-trippable timestamp.");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.True(parsed > DateTimeOffset.UtcNow, $"dueAtUtc '{dueAtUtc}' must be in the future.");
    }

    [Fact]
    public async Task ReminderScenarioRequestsTheToolAgainOnASecondTurnAfterARejection()
    {
        var factory = new FakeLanguageProviderFactory();
        Assert.True(factory.TryCreate("reminder", out var provider));

        // A second user turn in the same conversation: the first turn's create_reminder result
        // is still in history. The fake must ask again, not replay the stale outcome.
        var secondTurn = new LanguageProviderRequest(
            Guid.NewGuid(),
            new ConversationMessage[]
            {
                new(ConversationRole.User, "crear un recordatorio"),
                new(ConversationRole.Tool, ToolResult: new ToolResultMessage(
                    "fake-reminder-call-1",
                    CreateReminderTool.ToolName,
                    "The user rejected this tool call.",
                    true)),
                new(ConversationRole.Assistant, "The reminder was not created."),
                new(ConversationRole.User, "crear un recordatorio"),
            },
            []);

        var response = await provider!.GetResponseAsync(secondTurn, CancellationToken.None);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(CreateReminderTool.ToolName, call.Name);
        Assert.Null(response.Content);
    }
}
