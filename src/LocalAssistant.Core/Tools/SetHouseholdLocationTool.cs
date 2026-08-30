using System.Text.Json;
using LocalAssistant.Core.Profiles;

namespace LocalAssistant.Core.Tools;

public sealed class SetHouseholdLocationTool : ITool
{
    public const string ToolName = "set_household_location";
    private const string Source = "explicit_user_request";

    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            location = new { type = "string", minLength = 1, maxLength = HouseholdProfile.MaximumLocationLength, description = "The household location to persist after confirmation." },
            timeZoneId = new { type = "string", minLength = 1, maxLength = 128, description = "A canonical IANA time-zone identifier, for example Europe/Madrid." },
        },
        required = new[] { "location", "timeZoneId" },
        additionalProperties = false,
    });

    private readonly IHouseholdProfileStore _profiles;

    public SetHouseholdLocationTool(IHouseholdProfileStore profiles)
    {
        _profiles = profiles;
    }

    public ToolDefinition Definition { get; } = new(
        new ToolMetadata(
            ToolName,
            "Persists the household location and time zone only after explicit confirmation. Do not use for temporary travel or an inferred location.",
            new ToolRiskProfile(
                ToolOperationImpact.ChangesState,
                ToolDataSensitivity.Private,
                ToolExposure.Local,
                ToolCost.None,
                RequiresConfirmation: true,
                ["household.profile.write"])),
        InputSchema);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryReadArguments(arguments, out var location, out var timeZoneId, out var error))
        {
            return ToolExecutionResult.Failure("invalid_tool_arguments", error, "The household location is invalid.");
        }

        try
        {
            var profile = await _profiles.SetLocationAsync(location, timeZoneId, Source, cancellationToken);
            return ToolExecutionResult.Success(JsonSerializer.Serialize(new { profile.Location, profile.TimeZoneId }));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                         TimeZoneNotFoundException or
                                         InvalidTimeZoneException)
        {
            return ToolExecutionResult.Failure("invalid_tool_arguments", "The household location or time zone is invalid.", "The household location is invalid.");
        }
    }

    private static bool TryReadArguments(
        JsonElement arguments,
        out string location,
        out string timeZoneId,
        out string error)
    {
        location = string.Empty;
        timeZoneId = string.Empty;
        error = "The household location arguments are invalid.";
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property => property.Name is not ("location" or "timeZoneId")) ||
            !arguments.TryGetProperty("location", out var locationElement) ||
            !arguments.TryGetProperty("timeZoneId", out var timeZoneElement) ||
            locationElement.ValueKind != JsonValueKind.String ||
            timeZoneElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        location = locationElement.GetString() ?? string.Empty;
        timeZoneId = timeZoneElement.GetString() ?? string.Empty;
        return true;
    }
}
