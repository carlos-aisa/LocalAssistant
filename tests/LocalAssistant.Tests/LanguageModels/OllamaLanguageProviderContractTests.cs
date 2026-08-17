using System.Net;
using System.Text;
using System.Text.Json;
using LocalAssistant.Core.LanguageModels;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Tests.LanguageModels;

public sealed class OllamaLanguageProviderContractTests : LanguageProviderContractTests
{
    protected override ProviderLease CreateProvider(LanguageProviderResponse response)
    {
        var responseJson = CreateResponseJson(response);
        var httpClient = new HttpClient(new StaticResponseHandler(responseJson));
        var provider = new OllamaLanguageProvider(
            httpClient,
            Options.Create(new OllamaOptions
            {
                Endpoint = new Uri("http://localhost:11434"),
                Model = "contract-model",
            }));
        return new ProviderLease(provider, httpClient);
    }

    private static string CreateResponseJson(LanguageProviderResponse response)
    {
        object message = response.ToolCalls.Count == 0
            ? new { role = "assistant", content = response.Content }
            : new
            {
                role = "assistant",
                content = string.Empty,
                tool_calls = response.ToolCalls.Select(call => new
                {
                    function = new
                    {
                        name = call.Name,
                        arguments = call.Arguments,
                    },
                }),
            };

        return JsonSerializer.Serialize(new { message });
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public StaticResponseHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responseJson,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
