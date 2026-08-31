namespace LocalAssistant.Tests.Documentation;

public sealed class ChatScriptTests
{
    [Fact]
    public void ClientRequiresLoopbackAndPersistsOnlyAfterOpeningASession()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", "Chat.ps1");
        var script = File.ReadAllText(path);

        Assert.Contains("$Uri.IsLoopback", script, StringComparison.Ordinal);
        Assert.Contains("if (-not (Open-PrivateSession))", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("if (-not (Open-PrivateSession))", StringComparison.Ordinal) <
            script.IndexOf("Save-PrivateClientCredential $privateClientId", StringComparison.Ordinal));
        Assert.DoesNotContain("X-LocalAssistant-Api-Key", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallingEvaluatorUsesPrivateBearerInsteadOfTheLegacyApiKey()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", "Evaluate-OllamaToolCalling.ps1");
        var script = File.ReadAllText(path);

        Assert.Contains("LOCALASSISTANT_ACCESS_TOKEN", script, StringComparison.Ordinal);
        Assert.Contains("$headers[\"Authorization\"]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALASSISTANT_API_KEY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("X-LocalAssistant-Api-Key", script, StringComparison.Ordinal);
    }
}
