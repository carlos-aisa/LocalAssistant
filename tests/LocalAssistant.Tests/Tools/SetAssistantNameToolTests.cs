using System.Text.Json;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.Tools;

public sealed class SetAssistantNameToolTests
{
    [Fact]
    public async Task ChangesTheConfiguredDisplayName()
    {
        var profiles = new InMemoryAssistantProfileStore();
        var tool = new SetAssistantNameTool(profiles);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { displayName = "Jarvis" }),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Jarvis", (await profiles.GetAsync(CancellationToken.None)).DisplayName);
        Assert.Equal("installation.owner", Assert.Single(tool.Definition.Metadata.Risk.RequiredScopes));
        Assert.True(tool.Definition.Metadata.Risk.RequiresConfirmation);
    }

    [Fact]
    public async Task RejectsAdditionalArguments()
    {
        var tool = new SetAssistantNameTool(new InMemoryAssistantProfileStore());

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                displayName = "Jarvis",
                unexpected = true,
            }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task RejectsInvalidDisplayNamesWithoutChangingTheCurrentProfile()
    {
        var profiles = new InMemoryAssistantProfileStore();
        var tool = new SetAssistantNameTool(profiles);
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { displayName = "Jarvis" }),
            CancellationToken.None);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { displayName = " " }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
        Assert.Equal("Jarvis", (await profiles.GetAsync(CancellationToken.None)).DisplayName);
    }

    [Fact]
    public void RequiresTheInstallationOwnerScopeAndConfirmation()
    {
        var tool = new SetAssistantNameTool(new InMemoryAssistantProfileStore());
        var policy = new DefaultToolRiskPolicy();

        var denied = policy.Evaluate(
            tool.Definition.Metadata,
            new ToolPolicyContext("authenticated-user", new HashSet<string>(StringComparer.Ordinal)));
        var requiresConfirmation = policy.Evaluate(
            tool.Definition.Metadata,
            new ToolPolicyContext(
                "owner",
                new HashSet<string>(StringComparer.Ordinal) { "installation.owner" }));

        Assert.Equal(ToolPolicyDecisionKind.Denied, denied.Kind);
        Assert.Equal("scope_not_granted", denied.Code);
        Assert.Equal(ToolPolicyDecisionKind.RequiresConfirmation, requiresConfirmation.Kind);
    }

    private sealed class InMemoryAssistantProfileStore : IAssistantProfileStore
    {
        private AssistantProfile _profile = AssistantProfile.Default;

        public ValueTask<AssistantProfile> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profile);
        }

        public ValueTask<AssistantProfile> SetDisplayNameAsync(
            string displayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profile = AssistantProfile.Create(displayName);
            return ValueTask.FromResult(_profile);
        }
    }
}
