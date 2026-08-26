using System.Text.Json;

namespace LocalAssistant.Core.Tools;

public sealed class CurrentTimeTool : ITool
{
    public const string ToolName = "get_current_time";

    private static readonly JsonElement EmptyObjectSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false,
    });

    private readonly TimeProvider _timeProvider;

    private static readonly ToolMetadata _toolMetadata = 
        new(ToolName,
            "Returns the current UTC date and time.",
            ToolRiskProfile.PublicLocalRead);

    public ToolDefinition Definition {get;} =  new(
                _toolMetadata,
                EmptyObjectSchema);

    public CurrentTimeTool(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
        {
            return ValueTask.FromResult(ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                "The time tool accepts an empty JSON object only.",
                "The time tool arguments are invalid."));
        }

        var content = JsonSerializer.Serialize(new
        {
            utc = _timeProvider.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });

        return ValueTask.FromResult(ToolExecutionResult.Success(content));
    }
}
