using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Tests.TestDoubles;

namespace LocalAssistant.Tests.Orchestration;

public sealed class AuthoritativeCurrentTimeTests
{
    [Theory]
    [InlineData(2026, 8, 30, 12, 4, 2)]
    [InlineData(2026, 1, 15, 12, 4, 1)]
    public async Task ResolvesTheAuthorizedHouseholdTimeZoneWithTheCorrectSeasonalOffset(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int expectedOffsetHours)
    {
        var utc = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        var householdProfile = HouseholdProfile.Create(
            "Oviedo",
            "Europe/Madrid",
            utc,
            "test");
        var sut = new AuthoritativeCurrentTimeResolver(
            new ManualTimeProvider(utc),
            new StaticHouseholdProfileStore(householdProfile));
        var policyContext = new ToolPolicyContext(
            "owner",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "household.profile.read",
            });

        var result = await sut.ResolveAsync(policyContext, CancellationToken.None);

        Assert.Equal("Europe/Madrid", result.TimeZoneId);
        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), result.UtcOffset);
        Assert.True(result.UsesHouseholdTimeZone);
        Assert.Contains(
            $"utcOffset=+{expectedOffsetHours:00}:00",
            result.ToSystemMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesUtcWhenTheHouseholdTimeZoneIsNotAuthorized()
    {
        var utc = new DateTimeOffset(2026, 8, 30, 12, 4, 0, TimeSpan.Zero);
        var householdProfile = HouseholdProfile.Create(
            "Oviedo",
            "Europe/Madrid",
            utc,
            "test");
        var sut = new AuthoritativeCurrentTimeResolver(
            new ManualTimeProvider(utc),
            new StaticHouseholdProfileStore(householdProfile));
        var policyContext = new ToolPolicyContext(
            "owner",
            new HashSet<string>(StringComparer.Ordinal));

        var result = await sut.ResolveAsync(policyContext, CancellationToken.None);

        Assert.Equal("UTC", result.TimeZoneId);
        Assert.Equal(utc, result.Local);
        Assert.Equal(TimeSpan.Zero, result.UtcOffset);
        Assert.False(result.UsesHouseholdTimeZone);
        Assert.Contains("only available local-time value", result.ToSystemMessage(), StringComparison.Ordinal);
    }

    private sealed class StaticHouseholdProfileStore(HouseholdProfile profile) : IHouseholdProfileStore
    {
        public ValueTask<HouseholdProfile?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<HouseholdProfile?>(profile);
        }

        public ValueTask<HouseholdProfile> SetLocationAsync(
            string location,
            string timeZoneId,
            string source,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<HouseholdProfile>(
                new InvalidOperationException("The test store is read-only."));
    }
}
