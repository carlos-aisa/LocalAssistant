using System.Text.Json;
using LocalAssistant.Core.Profiles;

namespace LocalAssistant.Core.Tools;

public sealed class SetAssistantNameTool : ITool
{
    public const string ToolName = "set_assistant_name";

    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            displayName = new
            {
                type = "string",
                minLength = 1,
                maxLength = AssistantProfile.MaximumDisplayNameLength,
                description = "The assistant display name for this installation.",
            },
        },
        required = new[] { "displayName" },
        additionalProperties = false,
    });

    private readonly IAssistantProfileStore _profiles;

    public SetAssistantNameTool(IAssistantProfileStore profiles)
    {
        _profiles = profiles;
    }

    public ToolDefinition Definition { get; } = new(
        new ToolMetadata(
            ToolName,
            "Changes the assistant display name for this installation after confirmation.",
            new ToolRiskProfile(
                ToolOperationImpact.ChangesState,
                ToolDataSensitivity.Private,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: true,
                ["installation.owner"])),
        InputSchema);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadDisplayName(arguments, out var displayName, out var error))
        {
            return ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                error,
                "The assistant name is invalid.");
        }

        AssistantProfile profile;
        try
        {
            profile = await _profiles.SetDisplayNameAsync(displayName, cancellationToken);
        }
        catch (ArgumentException)
        {
            return ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                "The argument 'displayName' is invalid.",
                "The assistant name is invalid.");
        }

        return ToolExecutionResult.Success(JsonSerializer.Serialize(new
        {
            displayName = profile.DisplayName,
        }));
    }

    private static bool TryReadDisplayName(
        JsonElement arguments,
        out string displayName,
        out string error)
    {
        displayName = string.Empty;
        error = "The assistant name arguments are invalid.";

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "The assistant name arguments must be a JSON object.";
            return false;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name != "displayName")
            {
                error = $"The argument '{property.Name}' is not supported.";
                return false;
            }
        }

        if (!arguments.TryGetProperty("displayName", out var displayNameElement) ||
            displayNameElement.ValueKind != JsonValueKind.String)
        {
            error = "The argument 'displayName' must be a string.";
            return false;
        }

        displayName = displayNameElement.GetString() ?? string.Empty;
        return true;
    }
}
