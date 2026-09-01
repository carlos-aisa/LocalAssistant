using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAssistant.TerminalClient;

public sealed class PrivateApiClient
{
    private const int ServerErrorStatusCodeStart = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _httpClient;

    public PrivateApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ClientResult<HealthResponse>> CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "health");
        return await SendAsync(
            request,
            static _ => new HealthResponse(),
            "The LocalAssistant API is unavailable.",
            isTurnRequest: false,
            cancellationToken);
    }

    public async Task<ClientResult<PrivateSessionResponse>> CreateSessionAsync(
        string clientId,
        string credential,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/private/sessions")
        {
            Content = JsonContent.Create(
                new CreatePrivateSessionRequest(clientId, credential),
                options: JsonOptions),
        };

        return await SendAsync(
            request,
            ValidateSession,
            "The private-client session could not be opened.",
            isTurnRequest: false,
            cancellationToken);
    }

    public async Task<ClientResult<ConversationResponse>> SendMessageAsync(
        string accessToken,
        SendMessageRequest message,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/conversations/messages")
        {
            Content = JsonContent.Create(message, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await SendAsync(
            request,
            ValidateConversation,
            "The conversation request could not be completed.",
            isTurnRequest: true,
            cancellationToken);
    }

    private async Task<ClientResult<T>> SendAsync<T>(
        HttpRequestMessage request,
        Func<JsonElement, T?> deserialize,
        string connectionErrorMessage,
        bool isTurnRequest,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Accepted)
            {
                return ClientResults.Failure<T>(
                    GetErrorCode(response.StatusCode),
                    GetErrorMessage(response.StatusCode),
                    isTurnRequest && IsUncertainStatus(response.StatusCode));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var value = deserialize(document.RootElement);
            return value is null
                ? ClientResults.Failure<T>("invalid_response", "The API returned an invalid response.")
                : ClientResults.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientResults.Failure<T>(
                "request_cancelled",
                "The request was cancelled.",
                isTurnRequest);
        }
        catch (OperationCanceledException)
        {
            return ClientResults.Failure<T>(
                "request_timeout",
                "The request timed out.",
                isTurnRequest);
        }
        catch (HttpRequestException)
        {
            return ClientResults.Failure<T>(
                "connection_error",
                connectionErrorMessage,
                isTurnRequest);
        }
        catch (JsonException)
        {
            return ClientResults.Failure<T>("invalid_response", "The API returned an invalid response.");
        }
    }

    private static PrivateSessionResponse? ValidateSession(JsonElement root)
    {
        var response = root.Deserialize<PrivateSessionResponse>(JsonOptions);
        return response is null || string.IsNullOrWhiteSpace(response.AccessToken)
            ? null
            : response;
    }

    private static ConversationResponse? ValidateConversation(JsonElement root)
    {
        var response = root.Deserialize<ConversationResponse>(JsonOptions);
        return response is null || response.ConversationId == Guid.Empty || response.Tools is null
            ? null
            : response;
    }

    private static bool IsUncertainStatus(HttpStatusCode statusCode) =>
        (int)statusCode >= ServerErrorStatusCodeStart;

    private static string GetErrorCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => "validation_error",
        HttpStatusCode.Unauthorized => "authentication_failed",
        HttpStatusCode.Forbidden => "authorization_failed",
        HttpStatusCode.NotFound => "not_found",
        HttpStatusCode.Conflict => "conflict",
        HttpStatusCode.UnprocessableEntity => "validation_error",
        HttpStatusCode.BadGateway => "provider_error",
        HttpStatusCode.GatewayTimeout => "provider_timeout",
        _ => "http_error",
    };

    private static string GetErrorMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "Authentication was rejected by the API.",
        HttpStatusCode.Forbidden => "The private client is not authorized for this operation.",
        HttpStatusCode.NotFound => "The requested resource was not found.",
        HttpStatusCode.Conflict => "The conversation is not ready for another message.",
        HttpStatusCode.BadGateway => "The configured language provider failed.",
        HttpStatusCode.GatewayTimeout => "The configured language provider timed out.",
        _ => "The API rejected the request.",
    };
}
