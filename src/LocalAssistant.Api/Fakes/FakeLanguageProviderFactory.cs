using System.Globalization;
using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Api.Fakes;

public sealed class FakeLanguageProviderFactory
{
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonElement TemperatureArguments = JsonSerializer.SerializeToElement(new
    {
        value = 100,
        fromUnit = "celsius",
        toUnit = "fahrenheit",
    });
    // Computed per turn so the demo never rots: create_reminder rejects a dueAtUtc that is
    // not in the future, and a hard-coded date silently breaks the scenario once wall-clock
    // time passes it (test factories freeze the clock, so only real runs are affected).
    private static JsonElement CreateReminderArguments() => JsonSerializer.SerializeToElement(new
    {
        title = "Review the local reminder design",
        dueAtUtc = DateTimeOffset.UtcNow.AddDays(7).ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture),
    });

    public bool TryCreate(string scenario, out ILanguageProvider? provider)
    {
        provider = scenario switch
        {
            "direct" => CreateDirectProvider(),
            "time" => CreateTimeProvider(),
            "temperature" => CreateTemperatureProvider(),
            "reminder" => CreateReminderProvider(),
            _ => null,
        };

        return provider is not null;
    }

    private static ScriptedLanguageProvider CreateDirectProvider()
    {
        return new ScriptedLanguageProvider(
        [
            request =>
            {
                var message = request.Messages.Last(item => item.Role == ConversationRole.User).Content;
                return LanguageProviderResponse.Final($"Fake response: {message}");
            },
        ],
        "fake-direct");
    }

    private static ScriptedLanguageProvider CreateTimeProvider()
    {
        return new ScriptedLanguageProvider(
        [
            ScriptedLanguageProvider.Return(LanguageProviderResponse.RequestTools(
                new ToolCall("fake-time-call-1", CurrentTimeTool.ToolName, EmptyArguments))),
            request =>
            {
                var result = request.Messages.Last(item => item.ToolResult is not null).ToolResult!;
                using var document = JsonDocument.Parse(result!.Content);
                var utc = document.RootElement.GetProperty("utc").GetString();
                return LanguageProviderResponse.Final($"Current UTC time is {utc}.");
            },
        ],
        "fake-time");
    }

    private static ScriptedLanguageProvider CreateTemperatureProvider()
    {
        return new ScriptedLanguageProvider(
        [
            TemperatureResponse,
            TemperatureResponse,
        ],
        "fake-temperature");
    }

    private static LanguageProviderResponse TemperatureResponse(LanguageProviderRequest request)
    {
        var result = CurrentTurnToolResult(request, TemperatureConversionTool.ToolName);
        if (result is null)
        {
            return LanguageProviderResponse.RequestTools(new ToolCall(
                "fake-temperature-call-1",
                TemperatureConversionTool.ToolName,
                TemperatureArguments));
        }

        if (result.IsError)
        {
            return LanguageProviderResponse.Final("Temperature conversion was not performed.");
        }

        using var document = JsonDocument.Parse(result.Content);
        var value = document.RootElement.GetProperty("value").GetDecimal();
        var unit = document.RootElement.GetProperty("unit").GetString();
        return LanguageProviderResponse.Final(
            $"100 Celsius is {value.ToString("G29", CultureInfo.InvariantCulture)} {unit}.");
    }

    private static ScriptedLanguageProvider CreateReminderProvider()
    {
        return new ScriptedLanguageProvider(
        [
            ReminderResponse,
            ReminderResponse,
        ],
        "fake-reminder");
    }

    private static LanguageProviderResponse ReminderResponse(LanguageProviderRequest request)
    {
        var result = CurrentTurnToolResult(request, CreateReminderTool.ToolName);
        if (result is null)
        {
            return LanguageProviderResponse.RequestTools(new ToolCall(
                "fake-reminder-call-1",
                CreateReminderTool.ToolName,
                CreateReminderArguments()));
        }

        if (result.IsError)
        {
            return LanguageProviderResponse.Final("The reminder was not created.");
        }

        using var document = JsonDocument.Parse(result.Content);
        var title = document.RootElement.GetProperty("title").GetString();
        return LanguageProviderResponse.Final(
            $"Temporary reminder record created for experimental testing: {title}. No notification has been scheduled.");
    }

    // Only a tool result from the current turn (after the most recent user message) means
    // "the tool already ran". Scanning the whole history makes a second turn in the same
    // conversation replay the first turn's stale result instead of asking again.
    private static ToolResultMessage? CurrentTurnToolResult(LanguageProviderRequest request, string toolName)
    {
        var messages = request.Messages;
        var lastUserMessage = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ConversationRole.User)
            {
                lastUserMessage = index;
                break;
            }
        }

        for (var index = messages.Count - 1; index > lastUserMessage; index--)
        {
            if (messages[index].ToolResult?.ToolName == toolName)
            {
                return messages[index].ToolResult;
            }
        }

        return null;
    }
}
