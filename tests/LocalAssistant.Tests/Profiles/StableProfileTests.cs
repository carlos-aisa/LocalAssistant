using System.Text.Json;
using LocalAssistant.Api.Profiles;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;
using LocalAssistant.Tests.Api;
using LocalAssistant.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.Profiles;

public sealed class StableProfileTests
{
    [Fact]
    public async Task FileStoresPersistPersonalAndHouseholdProfilesSeparately()
    {
        using var directory = new TemporaryInstallationStateDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero));
        using (var writer = CreateStore(directory.Path, clock))
        {
            await writer.SetPreferredNameAsync("owner-a", "Test user", "test", CancellationToken.None);
            await writer.SetLocationAsync("Test city", "Europe/Madrid", "test", CancellationToken.None);
        }

        using var reader = CreateStore(directory.Path, clock);
        Assert.Equal("Test user", (await reader.GetAsync("owner-a", CancellationToken.None))?.PreferredName);
        Assert.Equal("Test city", (await reader.GetAsync(CancellationToken.None))?.Location);
        Assert.Null(await reader.GetAsync("owner-b", CancellationToken.None));
    }

    [Fact]
    public async Task PreferredNameToolChangesOnlyTheAuthenticatedUserProfile()
    {
        var profiles = new InMemoryUserProfiles();
        var tool = new SetUserPreferredNameTool(profiles);

        var result = await tool.ExecuteAsync(
            new ToolExecutionContext(Guid.NewGuid(), "owner-a", null),
            JsonSerializer.SerializeToElement(new { preferredName = "Test user" }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Test user", (await profiles.GetAsync("owner-a", CancellationToken.None))?.PreferredName);
        Assert.Contains("profile.personal.write", tool.Definition.Metadata.Risk.RequiredScopes);
        Assert.Contains("never changes the assistant", tool.Definition.Metadata.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentTimeUsesAuthorizedHouseholdTimeZoneAndUtcOtherwise()
    {
        var profile = HouseholdProfile.Create("Test city", "Europe/Madrid", DateTimeOffset.UtcNow, "test");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var authorized = new CurrentTimeTool(
            clock,
            new StaticHouseholdProfiles(profile),
            new StaticPolicyContextAccessor(
                "owner-a",
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "household.profile.read",
                }));
        var anonymous = new CurrentTimeTool(clock, new StaticHouseholdProfiles(profile));

        using var authorizedResult = JsonDocument.Parse((await authorized.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None)).Content);
        using var anonymousResult = JsonDocument.Parse((await anonymous.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None)).Content);
        Assert.Equal("Europe/Madrid", authorizedResult.RootElement.GetProperty("timeZoneId").GetString());
        Assert.Equal("UTC", anonymousResult.RootElement.GetProperty("timeZoneId").GetString());
    }

    private static FileStableProfileStores CreateStore(string path, TimeProvider clock) => new(
        Options.Create(new InstallationIdentityOptions { StateDirectory = path }),
        clock);

    private sealed class InMemoryUserProfiles : IUserProfileStore
    {
        private readonly Dictionary<string, UserProfile> _profiles = new(StringComparer.Ordinal);

        public ValueTask<UserProfile?> GetAsync(string principalId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_profiles.GetValueOrDefault(principalId));

        public ValueTask<UserProfile> SetPreferredNameAsync(string principalId, string preferredName, string source, CancellationToken cancellationToken)
        {
            var profile = UserProfile.Create(principalId, preferredName, DateTimeOffset.UtcNow, source);
            _profiles[principalId] = profile;
            return ValueTask.FromResult(profile);
        }
    }

    private sealed class StaticHouseholdProfiles(HouseholdProfile profile) : IHouseholdProfileStore
    {
        public ValueTask<HouseholdProfile?> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult<HouseholdProfile?>(profile);
        public ValueTask<HouseholdProfile> SetLocationAsync(string location, string timeZoneId, string source, CancellationToken cancellationToken) => ValueTask.FromResult(profile);
    }

    private sealed class StaticPolicyContextAccessor(string principalId, IReadOnlySet<string> scopes) : IToolPolicyContextAccessor
    {
        public ToolPolicyContext GetCurrent() => new(principalId, scopes);
    }
}
