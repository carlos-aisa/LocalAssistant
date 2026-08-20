using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.LanguageModels;
using LocalAssistant.Core.Orchestration;

namespace LocalAssistant.Api.Endpoints;

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/conversations/messages", SendMessageAsync)
            .WithName("SendConversationMessage")
            .WithSummary("Sends one message through the explicit orchestration loop.");
        endpoints.MapPost("/api/conversations/{conversationId:guid}/tool-confirmations/{confirmationId:guid}/decisions", ResolveConfirmationAsync)
            .WithName("ResolveToolConfirmation")
            .WithSummary("Approves or rejects one server-held tool call.");

        return endpoints;
    }

    private static async Task<IResult> SendMessageAsync(
        SendMessageRequest request,
        LanguageProviderSelector providerSelector,
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

        var selection = await providerSelector.SelectAsync(
            request.Provider,
            request.Scenario,
            cancellationToken);
        if (!selection.IsSuccess || selection.Provider is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [selection.ErrorField ?? nameof(request.Provider)] =
                    [selection.ErrorMessage ?? "The language provider selection is invalid."],
            });
        }

        var result = await orchestrator.ProcessAsync(
            new ConversationTurnRequest(request.Message, request.ConversationId),
            selection.Provider,
            cancellationToken);
        var response = ConversationApiResponse.FromResult(result);

        return Results.Json(response, statusCode: GetStatusCode(result));
    }

    private static async Task<IResult> ResolveConfirmationAsync(Guid conversationId, Guid confirmationId, ResolveToolConfirmationRequest request, LanguageProviderSelector providerSelector, IConversationOrchestrator orchestrator, CancellationToken cancellationToken)
    {
        var selection = await providerSelector.SelectAsync(request.Provider, request.Scenario, cancellationToken);
        if (!selection.IsSuccess || selection.Provider is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { [selection.ErrorField ?? nameof(request.Provider)] = [selection.ErrorMessage ?? "The language provider selection is invalid."] });
        }
        var result = await orchestrator.ResolveConfirmationAsync(conversationId, confirmationId, request.Approved, selection.Provider, cancellationToken);
        return Results.Json(ConversationApiResponse.FromResult(result), statusCode: GetStatusCode(result));
    }

    private static int GetStatusCode(ConversationTurnResult result)
    {
        return result.Error?.Code switch
        {
            null when result.Confirmation is not null => StatusCodes.Status202Accepted,
            null => StatusCodes.Status200OK,
            "provider_timeout" or "tool_timeout" => StatusCodes.Status504GatewayTimeout,
            "provider_error" or "tool_execution_failed" => StatusCodes.Status502BadGateway,
            "authentication_required" => StatusCodes.Status401Unauthorized,
            "scope_not_granted" or "external_gateway_required" or "tool_policy_denied" => StatusCodes.Status403Forbidden,
            "tool_not_found" or "invalid_tool_arguments" =>
                StatusCodes.Status422UnprocessableEntity,
            "iteration_limit_reached" or "invalid_provider_response" or "confirmation_pending" or "confirmation_provider_mismatch" =>
                StatusCodes.Status409Conflict,
            "confirmation_not_found" => StatusCodes.Status404NotFound,
            "confirmation_expired" => StatusCodes.Status410Gone,
            _ => StatusCodes.Status500InternalServerError,
        };
    }
}
