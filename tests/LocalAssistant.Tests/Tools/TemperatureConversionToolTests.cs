using System.Text.Json;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.Tools;

public sealed class TemperatureConversionToolTests
{
    [Theory]
    [InlineData(0, "celsius", "fahrenheit", 32)]
    [InlineData(32, "fahrenheit", "celsius", 0)]
    [InlineData(273.15, "kelvin", "celsius", 0)]
    [InlineData(-40, "celsius", "fahrenheit", -40)]
    public async Task ConvertsSupportedTemperatures(
        decimal value,
        string fromUnit,
        string toUnit,
        decimal expectedValue)
    {
        var tool = new TemperatureConversionTool();
        var arguments = JsonSerializer.SerializeToElement(new
        {
            value,
            fromUnit,
            toUnit,
        });

        var result = await tool.ExecuteAsync(arguments, CancellationToken.None);

        Assert.True(result.IsSuccess);
        using var document = JsonDocument.Parse(result.Content);
        Assert.Equal(expectedValue, document.RootElement.GetProperty("value").GetDecimal());
        Assert.Equal(toUnit, document.RootElement.GetProperty("unit").GetString());
    }

    [Theory]
    [InlineData("""{"value":20,"fromUnit":"celsius"}""")]
    [InlineData("""{"value":20,"fromUnit":"celsius","toUnit":"rankine"}""")]
    [InlineData("""{"value":"20","fromUnit":"celsius","toUnit":"fahrenheit"}""")]
    [InlineData("""{"value":20,"fromUnit":"celsius","toUnit":"fahrenheit","extra":true}""")]
    public async Task RejectsInvalidArguments(string argumentsJson)
    {
        var tool = new TemperatureConversionTool();
        using var document = JsonDocument.Parse(argumentsJson);

        var result = await tool.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task RejectsTemperaturesBelowAbsoluteZero()
    {
        var tool = new TemperatureConversionTool();
        var arguments = JsonSerializer.SerializeToElement(new
        {
            value = -274,
            fromUnit = "celsius",
            toUnit = "kelvin",
        });

        var result = await tool.ExecuteAsync(arguments, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
        Assert.Equal("The temperature cannot be below absolute zero.", result.Content);
    }
}
