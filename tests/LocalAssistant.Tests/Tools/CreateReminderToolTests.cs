using System.Text.Json;
using LocalAssistant.Core.Reminders;
using LocalAssistant.Core.Tools;
using LocalAssistant.Tests.TestDoubles;

namespace LocalAssistant.Tests.Tools;

public sealed class CreateReminderToolTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DefinitionAndResultDeclareTheTemporaryNonNotifyingSemantics()
    {
        var tool = CreateTool(out _);

        Assert.Contains("temporary", tool.Definition.Metadata.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in-memory", tool.Definition.Metadata.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not schedule or deliver a notification", tool.Definition.Metadata.Description, StringComparison.OrdinalIgnoreCase);

        var result = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "owner", Guid.NewGuid()),
            Arguments("Prepare the presentation"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var document = JsonDocument.Parse(result.Content);
        var root = document.RootElement;
        Assert.Equal("in_memory", root.GetProperty("storage").GetString());
        Assert.False(root.GetProperty("durable").GetBoolean());
        Assert.False(root.GetProperty("notificationScheduled").GetBoolean());
    }

    [Fact]
    public async Task RepeatedOperationReturnsTheOriginalReminder()
    {
        var tool = CreateTool(out _);
        var operationId = Guid.NewGuid();
        var arguments = Arguments("Prepare the presentation");
        var context = new ToolExecutionContext(Guid.NewGuid(), "owner", operationId);

        var first = await tool.ExecuteAsync(context, arguments, CancellationToken.None);
        var second = await tool.ExecuteAsync(context, arguments, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(ReminderId(first.Content), ReminderId(second.Content));
        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public async Task DifferentOperationsCreateDifferentRemindersWithTheSameArguments()
    {
        var tool = CreateTool(out _);
        var arguments = Arguments("Prepare the presentation");

        var first = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "owner", Guid.NewGuid()),
            arguments,
            CancellationToken.None);
        var second = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "owner", Guid.NewGuid()),
            arguments,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(ReminderId(first.Content), ReminderId(second.Content));
    }

    [Fact]
    public async Task SameOperationIdIsIsolatedByPrincipal()
    {
        var tool = CreateTool(out _);
        var operationId = Guid.NewGuid();
        var arguments = Arguments("Prepare the presentation");

        var first = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "first-owner", operationId),
            arguments,
            CancellationToken.None);
        var second = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "second-owner", operationId),
            arguments,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(ReminderId(first.Content), ReminderId(second.Content));
    }

    [Fact]
    public async Task ConcurrentExecutionsOfTheSameOperationCreateOneReminder()
    {
        var tool = CreateTool(out _);
        var context = new ToolExecutionContext(Guid.NewGuid(), "owner", Guid.NewGuid());
        var arguments = Arguments("Prepare the presentation");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => tool.ExecuteAsync(context, arguments, CancellationToken.None).AsTask()));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results.Select(result => ReminderId(result.Content)).Distinct());
    }

    [Fact]
    public async Task PastReminderTimeIsRejected()
    {
        var tool = CreateTool(out _);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            title = "Prepare the presentation",
            dueAtUtc = "2026-08-29T09:59:59Z",
        });

        var result = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "owner", Guid.NewGuid()),
            arguments,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
    }

    private static CreateReminderTool CreateTool(out InMemoryReminderStore reminders)
    {
        var clock = new ManualTimeProvider(FixedUtcNow);
        reminders = new InMemoryReminderStore(clock);
        return new CreateReminderTool(reminders, clock);
    }

    private static JsonElement Arguments(string title) => JsonSerializer.SerializeToElement(new
    {
        title,
        dueAtUtc = "2026-08-30T09:00:00Z",
    });

    private static Guid ReminderId(string content)
    {
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
