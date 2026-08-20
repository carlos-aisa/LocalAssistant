using System.Text.Json;

namespace LocalAssistant.Core.Tools;

public sealed class TemperatureConversionTool : ITool
{
    public const string ToolName = "convert_temperature";

    private const decimal KelvinOffset = 273.15m;

    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            value = new
            {
                type = "number",
                description = "Temperature value to convert.",
            },
            fromUnit = new
            {
                type = "string",
                @enum = new[] { "celsius", "fahrenheit", "kelvin" },
            },
            toUnit = new
            {
                type = "string",
                @enum = new[] { "celsius", "fahrenheit", "kelvin" },
            },
        },
        required = new[] { "value", "fromUnit", "toUnit" },
        additionalProperties = false,
    });

    public ToolDefinition Definition { get; } = new(
        new ToolMetadata(
            ToolName,
            "Converts a temperature between Celsius, Fahrenheit, and Kelvin.",
            ToolImpact.ReadOnly,
            RequiresConfirmation: false),
        InputSchema);

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadArguments(arguments, out var value, out var fromUnit, out var toUnit, out var error))
        {
            return ValueTask.FromResult(ToolExecutionResult.Failure("invalid_tool_arguments", error));
        }

        var kelvin = ConvertToKelvin(value, fromUnit);
        if (kelvin < decimal.Zero)
        {
            return ValueTask.FromResult(ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                "The temperature cannot be below absolute zero."));
        }

        var convertedValue = decimal.Round(
            ConvertFromKelvin(kelvin, toUnit),
            decimals: 6,
            MidpointRounding.AwayFromZero);
        var content = JsonSerializer.Serialize(new
        {
            value = convertedValue,
            unit = ToUnitName(toUnit),
        });

        return ValueTask.FromResult(ToolExecutionResult.Success(content));
    }

    private static bool TryReadArguments(
        JsonElement arguments,
        out decimal value,
        out TemperatureUnit fromUnit,
        out TemperatureUnit toUnit,
        out string error)
    {
        value = default;
        fromUnit = default;
        toUnit = default;
        error = "The temperature conversion arguments are invalid.";

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "The temperature conversion arguments must be a JSON object.";
            return false;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("value" or "fromUnit" or "toUnit"))
            {
                error = $"The argument '{property.Name}' is not supported.";
                return false;
            }
        }

        if (!arguments.TryGetProperty("value", out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.Number ||
            !valueElement.TryGetDecimal(out value))
        {
            error = "The argument 'value' must be a decimal number.";
            return false;
        }

        if (!arguments.TryGetProperty("fromUnit", out var fromUnitElement) ||
            !TryParseUnit(fromUnitElement, out fromUnit))
        {
            error = "The argument 'fromUnit' must be celsius, fahrenheit, or kelvin.";
            return false;
        }

        if (!arguments.TryGetProperty("toUnit", out var toUnitElement) ||
            !TryParseUnit(toUnitElement, out toUnit))
        {
            error = "The argument 'toUnit' must be celsius, fahrenheit, or kelvin.";
            return false;
        }

        return true;
    }

    private static bool TryParseUnit(JsonElement element, out TemperatureUnit unit)
    {
        switch (element.ValueKind == JsonValueKind.String ? element.GetString() : null)
        {
            case "celsius":
                unit = TemperatureUnit.Celsius;
                return true;
            case "fahrenheit":
                unit = TemperatureUnit.Fahrenheit;
                return true;
            case "kelvin":
                unit = TemperatureUnit.Kelvin;
                return true;
            default:
                unit = default;
                return false;
        }
    }

    private static decimal ConvertToKelvin(decimal value, TemperatureUnit unit) => unit switch
    {
        TemperatureUnit.Celsius => value + KelvinOffset,
        TemperatureUnit.Fahrenheit => ((value - 32m) * 5m / 9m) + KelvinOffset,
        TemperatureUnit.Kelvin => value,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private static decimal ConvertFromKelvin(decimal value, TemperatureUnit unit) => unit switch
    {
        TemperatureUnit.Celsius => value - KelvinOffset,
        TemperatureUnit.Fahrenheit => ((value - KelvinOffset) * 9m / 5m) + 32m,
        TemperatureUnit.Kelvin => value,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private static string ToUnitName(TemperatureUnit unit) => unit switch
    {
        TemperatureUnit.Celsius => "celsius",
        TemperatureUnit.Fahrenheit => "fahrenheit",
        TemperatureUnit.Kelvin => "kelvin",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private enum TemperatureUnit
    {
        Celsius,
        Fahrenheit,
        Kelvin,
    }
}
