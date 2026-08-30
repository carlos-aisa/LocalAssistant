using System.Text.Json;
using LocalAssistant.Core.Profiles;

namespace LocalAssistant.Core.Tools;

public sealed class SetUserPreferredNameTool : ITool
{
    public const string ToolName = "set_user_preferred_name";
    private const string Source = "explicit_user_request";

    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            preferredName = new
            {
                type = "string",
                minLength = 1,
                maxLength = UserProfile.MaximumPreferredNameLength,
                description = "The authenticated user's preferred name. This never changes the assistant name.",
            },
        },
        required = new[] { "preferredName" },
        additionalProperties = false,
    });

    private readonly IUserProfileStore _profiles;

    public SetUserPreferredNameTool(IUserProfileStore profiles)
    {
        _profiles = profiles;
    }

    public ToolDefinition Definition { get; } = new(
        new ToolMetadata(
            ToolName,
            "Changes only the authenticated user's preferred name after confirmation. It never changes the assistant display name.",
            new ToolRiskProfile(
                ToolOperationImpact.ChangesState,
                ToolDataSensitivity.Private,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: true,
                ["profile.personal.write"])),
        InputSchema);

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionResult.Failure(
            "tool_context_required",
            "The authenticated user context is required.",
            "The preferred name could not be saved."));

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.PrincipalId))
        {
            return ToolExecutionResult.Failure(
                "tool_context_required",
                "The authenticated user context is required.",
                "The preferred name could not be saved.");
        }

        if (!TryReadPreferredName(arguments, out var preferredName, out var error))
        {
            return ToolExecutionResult.Failure("invalid_tool_arguments", error, "The preferred name is invalid.");
        }

        try
        {
            var profile = await _profiles.SetPreferredNameAsync(
                context.PrincipalId,
                preferredName,
                Source,
                cancellationToken);
            return ToolExecutionResult.Success(JsonSerializer.Serialize(new { profile.PreferredName }));
        }
        catch (ArgumentException)
        {
            return ToolExecutionResult.Failure("invalid_tool_arguments", "The preferred name is invalid.", "The preferred name is invalid.");
        }
    }

    private static bool TryReadPreferredName(JsonElement arguments, out string preferredName, out string error)
    {
        preferredName = string.Empty;
        error = "The preferred name arguments are invalid.";
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "The preferred name arguments must be a JSON object.";
            return false;
        }

        if (arguments.EnumerateObject().Any(property => property.Name != "preferredName") ||
            !arguments.TryGetProperty("preferredName", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        preferredName = name.GetString() ?? string.Empty;
        return true;
    }
}
