using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Core.Orchestration;

namespace LocalAssistant.Api.Endpoints;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/conversations/messages", SendMessageAsync)
            .WithName("SendConversationMessage")
            .WithSummary("Sends one message through the explicit orchestration loop.");

        return endpoints;
    }

    private static async Task<IResult> SendMessageAsync(
        SendMessageRequest request,
        FakeLanguageProviderFactory providerFactory,
        IConversationOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Message)] = ["A non-empty message is required."],
            });
        }

        if (!providerFactory.TryCreate(request.Scenario, out var provider) || provider is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Scenario)] = ["Supported fake scenarios are 'direct' and 'time'."],
            });
        }

        var approvedTools = request.ApprovedTools is null
            ? null
            : new HashSet<string>(request.ApprovedTools, StringComparer.Ordinal);
        var result = await orchestrator.ProcessAsync(
            new ConversationTurnRequest(request.Message, request.ConversationId, approvedTools),
            provider,
            cancellationToken);
        var response = ConversationApiResponse.FromResult(result);

        return Results.Json(response, statusCode: GetStatusCode(result));
    }

    private static int GetStatusCode(ConversationTurnResult result)
    {
        return result.Error?.Code switch
        {
            null => StatusCodes.Status200OK,
            "provider_timeout" or "tool_timeout" => StatusCodes.Status504GatewayTimeout,
            "provider_error" or "tool_execution_failed" => StatusCodes.Status502BadGateway,
            "tool_not_found" or "invalid_tool_arguments" or "tool_confirmation_required" =>
                StatusCodes.Status422UnprocessableEntity,
            "iteration_limit_reached" or "invalid_provider_response" =>
                StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
    }
}
