using System.Text.Json;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Security.ToolRisk;

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
    private readonly IHouseholdProfileStore _householdProfiles;
    private readonly IToolPolicyContextAccessor _policyContextAccessor;

    private static readonly ToolMetadata _toolMetadata =
        new(ToolName,
            "Returns the current date and time using the household time zone only when the caller is authorized, otherwise UTC.",
            ToolRiskProfile.PublicLocalRead);

    public ToolDefinition Definition { get; } = new(
                _toolMetadata,
                EmptyObjectSchema);

    public CurrentTimeTool(
        TimeProvider timeProvider,
        IHouseholdProfileStore? householdProfiles = null,
        IToolPolicyContextAccessor? policyContextAccessor = null)
    {
        _timeProvider = timeProvider;
        _householdProfiles = householdProfiles ?? new NullHouseholdProfileStore();
        _policyContextAccessor = policyContextAccessor ?? new AnonymousToolPolicyContextAccessor();
    }

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
        {
            return ToolExecutionResult.Failure(
                "invalid_tool_arguments",
                "The time tool accepts an empty JSON object only.",
                "The time tool arguments are invalid.");
        }

        var utcNow = _timeProvider.GetUtcNow();
        var policyContext = _policyContextAccessor.GetCurrent();
        var householdProfile = policyContext.IsAuthenticated &&
                               policyContext.GrantedScopes.Contains("household.profile.read")
            ? await _householdProfiles.GetAsync(cancellationToken)
            : null;
        var timeZone = householdProfile is null
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(householdProfile.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var content = JsonSerializer.Serialize(new
        {
            utc = utcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            local = local.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            timeZoneId = householdProfile?.TimeZoneId ?? "UTC",
            utcOffset = FormatUtcOffset(local.Offset),
        });

        return ToolExecutionResult.Success(content);
    }

    private static string FormatUtcOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{sign}{offset.Duration():hh\\:mm}";
    }
}
