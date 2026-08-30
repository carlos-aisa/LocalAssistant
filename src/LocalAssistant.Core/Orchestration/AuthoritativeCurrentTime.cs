using System.Globalization;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Security.ToolRisk;

namespace LocalAssistant.Core.Orchestration;

public static class CurrentTimeRequestPolicy
{
    private static readonly string[] CurrentTimePhrases =
    [
        "qué hora",
        "que hora",
        "dime la hora",
        "hora local",
        "qué día es hoy",
        "que dia es hoy",
        "darme la hora",
        "what time is it",
        "current time",
        "current date",
    ];

    private static readonly string[] ExcludedPhrases =
    [
        "qué es utc",
        "que es utc",
        "husos horarios",
        "hora de la reunión",
        "hora de la reunion",
        "get_current_time",
        "convierte las",
        "convert ",
    ];

    public static bool RequiresAuthoritativeTime(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim();
        return !ExcludedPhrases.Any(phrase =>
                   normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase)) &&
               CurrentTimePhrases.Any(phrase =>
                   normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record AuthoritativeCurrentTime(
    DateTimeOffset Utc,
    DateTimeOffset Local,
    string TimeZoneId,
    TimeSpan UtcOffset,
    bool UsesHouseholdTimeZone)
{
    public string ToSystemMessage()
    {
        var offsetSign = UtcOffset < TimeSpan.Zero ? "-" : "+";
        var formattedOffset = $"{offsetSign}{UtcOffset.Duration():hh\\:mm}";
        var limitation = UsesHouseholdTimeZone
            ? ""
            : " No household time zone is authorized, so UTC is the only available local-time value.";

        return
            $"Authoritative current time supplied by the server: UTC={Utc.ToString("O", CultureInfo.InvariantCulture)}; " +
            $"local={Local.ToString("O", CultureInfo.InvariantCulture)}; timeZone={TimeZoneId}; " +
            $"utcOffset={formattedOffset}; householdTimeZoneAuthorized={UsesHouseholdTimeZone}. " +
            "Use these exact values for any current date or time in the response. Do not claim Internet access, training-data access, or a tool execution unless one is present in the conversation." +
            limitation;
    }
}

public sealed class AuthoritativeCurrentTimeResolver
{
    private readonly TimeProvider _clock;
    private readonly IHouseholdProfileStore _householdProfiles;

    public AuthoritativeCurrentTimeResolver(
        TimeProvider clock,
        IHouseholdProfileStore? householdProfiles = null)
    {
        _clock = clock;
        _householdProfiles = householdProfiles ?? new NullHouseholdProfileStore();
    }

    public async ValueTask<AuthoritativeCurrentTime> ResolveAsync(
        ToolPolicyContext policyContext,
        CancellationToken cancellationToken)
    {
        var utc = _clock.GetUtcNow();
        var householdProfile = policyContext.IsAuthenticated &&
                               policyContext.GrantedScopes.Contains("household.profile.read")
            ? await _householdProfiles.GetAsync(cancellationToken)
            : null;
        var timeZone = householdProfile is null
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(householdProfile.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(utc, timeZone);
        return new AuthoritativeCurrentTime(
            utc,
            local,
            householdProfile?.TimeZoneId ?? "UTC",
            local.Offset,
            householdProfile is not null);
    }
}
