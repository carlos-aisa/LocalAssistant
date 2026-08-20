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

    public bool TryCreate(string scenario, out ILanguageProvider? provider)
    {
        provider = scenario switch
        {
            "direct" => CreateDirectProvider(),
            "time" => CreateTimeProvider(),
            "temperature" => CreateTemperatureProvider(),
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
        var toolMessage = request.Messages.LastOrDefault(
            item => item.ToolResult?.ToolName == TemperatureConversionTool.ToolName);
        var result = toolMessage?.ToolResult;
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
}
