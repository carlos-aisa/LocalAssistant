using System.Security.Claims;
using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.LanguageModels;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Infrastructure.Conversations;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Endpoints;

public static class ConversationEndpoints
{
    private const string DeleteConfirmationHeader = "X-LocalAssistant-Confirm-Delete";
    private const string ConversationReadScope = "conversations.read";

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/conversations/messages", SendMessageAsync)
            .WithName("SendConversationMessage")
            .WithSummary("Sends one message through the explicit orchestration loop.");
        endpoints.MapPost("/api/conversations/{conversationId:guid}/tool-confirmations/{confirmationId:guid}/decisions", ResolveConfirmationAsync)
            .WithName("ResolveToolConfirmation")
            .WithSummary("Approves or rejects one server-held tool call.");
        endpoints.MapDelete("/api/conversations/{conversationId:guid}", DeleteAsync)
            .WithName("DeleteConversation")
            .WithSummary("Deletes one authenticated principal's persisted conversation.");
        endpoints.MapPost("/api/conversations/{conversationId:guid}/completion", CompleteAsync)
            .WithName("CompleteConversation")
            .WithSummary("Marks one authenticated conversation eligible for immediate background indexing.");
        endpoints.MapGet("/api/conversations", ListAsync)
            .WithName("ListConversations")
            .WithSummary("Lists the authenticated principal's persisted conversations.");
        endpoints.MapGet("/api/conversations/{conversationId:guid}", GetDetailsAsync)
            .WithName("GetConversationDetails")
            .WithSummary("Gets public metadata for one authenticated principal's persisted conversation.");
        endpoints.MapGet("/api/conversations/{conversationId:guid}/history", GetHistoryAsync)
            .WithName("GetConversationHistory")
            .WithSummary("Gets the sanitized public history for one authenticated principal's conversation.");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ListConversationsRequest request,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        SqliteConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        var authorization = AuthorizeConversationRead(httpContext, persistenceOptions.Value.Enabled);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            var page = await conversationStore.ListOwnedAsync(
                authorization.OwnerPrincipalId!,
                request.Cursor,
                request.Limit ?? SqliteConversationStore.MaximumListPageSize,
                cancellationToken);
            return Results.Ok(new ConversationPageResponse<ConversationSummaryResponse>(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor));
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [exception.ParamName ?? "query"] = [exception.Message],
            });
        }
    }

    private static async Task<IResult> GetDetailsAsync(
        Guid conversationId,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        SqliteConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        var authorization = AuthorizeConversationRead(httpContext, persistenceOptions.Value.Enabled);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        var details = await conversationStore.GetOwnedDetailsAsync(
            conversationId,
            authorization.OwnerPrincipalId!,
            cancellationToken);
        return details is null ? Results.NotFound() : Results.Ok(ToResponse(details));
    }

    private static async Task<IResult> GetHistoryAsync(
        Guid conversationId,
        [AsParameters] ConversationHistoryRequest request,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        SqliteConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        var authorization = AuthorizeConversationRead(httpContext, persistenceOptions.Value.Enabled);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            var page = await conversationStore.GetOwnedHistoryAsync(
                conversationId,
                authorization.OwnerPrincipalId!,
                request.Cursor,
                request.Limit ?? SqliteConversationStore.MaximumHistoryPageSize,
                cancellationToken);
            return page is null
                ? Results.NotFound()
                : Results.Ok(new ConversationPageResponse<PublicConversationMessageResponse>(
                    page.Items.Select(message => new PublicConversationMessageResponse(
                        message.Role.ToString().ToLowerInvariant(),
                        message.Content)).ToArray(),
                    page.NextCursor));
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [exception.ParamName ?? "query"] = [exception.Message],
            });
        }
    }

    private static ConversationReadAuthorization AuthorizeConversationRead(
        HttpContext httpContext,
        bool persistenceEnabled)
    {
        if (!persistenceEnabled)
        {
            return new(Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Conversation persistence is disabled."), null);
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return new(Results.Unauthorized(), null);
        }

        if (!httpContext.User.HasClaim(
                AuthorizationClaimTypes.Scope,
                ConversationReadScope))
        {
            return new(Results.Forbid(), null);
        }

        var ownerPrincipalId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(ownerPrincipalId)
            ? new(Results.Unauthorized(), null)
            : new(null, ownerPrincipalId);
    }

    private static ConversationSummaryResponse ToResponse(ConversationSummary summary) => new(
        summary.ConversationId,
        summary.Title,
        summary.LastActivityAtUtc,
        summary.IndexingRequestedAtUtc);

    private static ConversationDetailsResponse ToResponse(ConversationDetails details) => new(
        details.ConversationId,
        details.Title,
        details.LastActivityAtUtc,
        details.IndexingRequestedAtUtc);

    private sealed record ConversationReadAuthorization(IResult? Failure, string? OwnerPrincipalId);

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

    private static async Task<IResult> DeleteAsync(
        Guid conversationId,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        IConversationStore conversationStore,
        IConversationExecutionLock conversationLock,
        IToolConfirmationStore confirmationStore,
        CancellationToken cancellationToken)
    {
        if (!persistenceOptions.Value.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Conversation persistence is disabled.");
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        var ownerPrincipalId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerPrincipalId))
        {
            return Results.Unauthorized();
        }

        var confirmationValues = httpContext.Request.Headers[DeleteConfirmationHeader];
        if (confirmationValues.Count != 1 || !string.Equals(
                confirmationValues[0],
                "true",
                StringComparison.Ordinal))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [DeleteConfirmationHeader] = ["The delete confirmation header must be exactly true."],
            });
        }

        using var lockHandle = await conversationLock.AcquireAsync(
            conversationId,
            cancellationToken);
        var deleted = await conversationStore.DeleteOwnedAsync(
            conversationId,
            ownerPrincipalId,
            cancellationToken);
        if (!deleted)
        {
            return Results.NotFound();
        }

        await confirmationStore.RemoveAsync(conversationId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CompleteAsync(
        Guid conversationId,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        SqliteConversationStore conversationStore,
        CancellationToken cancellationToken)
    {
        if (!persistenceOptions.Value.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Conversation persistence is disabled.");
        }

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        var ownerPrincipalId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(ownerPrincipalId))
        {
            return Results.Unauthorized();
        }

        var completed = await conversationStore.RequestImmediateIndexingAsync(
            conversationId,
            ownerPrincipalId,
            cancellationToken);
        return completed ? Results.NoContent() : Results.NotFound();
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
            "conversation_not_found" or "confirmation_not_found" => StatusCodes.Status404NotFound,
            "confirmation_expired" => StatusCodes.Status410Gone,
            _ => StatusCodes.Status500InternalServerError,
        };
    }
}
