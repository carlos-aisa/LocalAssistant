using System.Text.Json;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Core.Tools;

namespace LocalAssistant.Tests.LanguageModels;

public abstract class LanguageProviderContractTests
{
    [Fact]
    public void NameIsStableAndNonEmpty()
    {
        using var provider = CreateProvider(LanguageProviderResponse.Final("Hello"));

        var first = provider.Instance.Name;
        var second = provider.Instance.Name;

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task FinalResponseContainsContentAndNoToolCalls()
    {
        const string expectedContent = "Contract response";
        using var provider = CreateProvider(LanguageProviderResponse.Final(expectedContent));

        var response = await provider.Instance.GetResponseAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Equal(expectedContent, response.Content);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task ToolResponseContainsStructuredCallAndNoFinalContent()
    {
        var arguments = ParseJson("""{ "timezone": "UTC" }""");
        var expectedCall = new ToolCall("contract-call", "get_current_time", arguments);
        using var provider = CreateProvider(
            LanguageProviderResponse.RequestTools(expectedCall));

        var response = await provider.Instance.GetResponseAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.Null(response.Content);
        var actualCall = Assert.Single(response.ToolCalls);
        Assert.False(string.IsNullOrWhiteSpace(actualCall.Id));
        Assert.Equal(expectedCall.Name, actualCall.Name);
        Assert.Equal(
            expectedCall.Arguments.GetProperty("timezone").GetString(),
            actualCall.Arguments.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task PreCancelledRequestThrowsCancellation()
    {
        using var provider = CreateProvider(LanguageProviderResponse.Final("unused"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.Instance.GetResponseAsync(CreateRequest(), cancellation.Token));
    }

    protected abstract ProviderLease CreateProvider(LanguageProviderResponse response);

    protected sealed class ProviderLease : IDisposable
    {
        private readonly IDisposable? _resource;

        public ProviderLease(ILanguageProvider instance, IDisposable? resource = null)
        {
            Instance = instance;
            _resource = resource;
        }

        public ILanguageProvider Instance { get; }

        public void Dispose()
        {
            _resource?.Dispose();
        }
    }

    private static LanguageProviderRequest CreateRequest()
    {
        return new LanguageProviderRequest(
            Guid.Parse("7690e337-bd99-46bc-8ec9-4c54b80b48dc"),
            [new ConversationMessage(ConversationRole.User, "Contract request")],
            [
                new ToolDefinition(
                    new ToolMetadata(
                        "get_current_time",
                        "Returns the current time.",
                        ToolImpact.ReadOnly,
                        RequiresConfirmation: false),
                    ParseJson("""{ "type": "object", "properties": {} }""")),
            ]);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
