using System.Security.Claims;
using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Memory;
using LocalAssistant.Infrastructure.Conversations;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Api.Endpoints;

public static class PersonalMemoryEndpoints
{
    private const string ReadScope = "memory.personal.read";
    private const string WriteScope = "memory.personal.write";

    public static IEndpointRouteBuilder MapPersonalMemoryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/memories/personal", CreateAsync)
            .WithName("CreatePersonalMemory")
            .WithSummary("Creates an explicit personal memory note.");
        endpoints.MapGet("/api/memories/personal", ListAsync)
            .WithName("ListPersonalMemories")
            .WithSummary("Lists the authenticated principal's personal memory notes.");
        endpoints.MapDelete("/api/memories/personal/{memoryId}", DeleteAsync)
            .WithName("DeletePersonalMemory")
            .WithSummary("Deletes one explicit personal memory note.");

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreatePersonalMemoryRequest request,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        IPersonalMemoryStore personalMemoryStore,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpContext, WriteScope, out var ownerPrincipalId);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var persistenceFailure = RequirePersistence(persistenceOptions.Value);
        if (persistenceFailure is not null)
        {
            return persistenceFailure;
        }

        PersonalMemoryDraft draft;
        try
        {
            draft = new PersonalMemoryDraft(request.Text);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(exception);
        }

        var memory = await personalMemoryStore.CreateAsync(
            ownerPrincipalId!,
            draft,
            cancellationToken);
        var response = PersonalMemoryResponse.FromMemory(memory);
        return Results.Created($"/api/memories/personal/{memory.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ListPersonalMemoriesRequest request,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        IPersonalMemoryStore personalMemoryStore,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpContext, ReadScope, out var ownerPrincipalId);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var persistenceFailure = RequirePersistence(persistenceOptions.Value);
        if (persistenceFailure is not null)
        {
            return persistenceFailure;
        }

        PersonalMemoryListQuery query;
        try
        {
            query = new PersonalMemoryListQuery(
                request.Limit ?? PersonalMemoryListQuery.DefaultLimit);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ValidationProblem(exception);
        }

        var memories = await personalMemoryStore.ListOwnedAsync(
            ownerPrincipalId!,
            query,
            cancellationToken);
        return Results.Ok(PersonalMemoryListResponse.FromMemories(memories));
    }

    private static async Task<IResult> DeleteAsync(
        string memoryId,
        HttpContext httpContext,
        IOptions<SqliteConversationStoreOptions> persistenceOptions,
        IPersonalMemoryStore personalMemoryStore,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpContext, WriteScope, out var ownerPrincipalId);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var persistenceFailure = RequirePersistence(persistenceOptions.Value);
        if (persistenceFailure is not null)
        {
            return persistenceFailure;
        }

        if (!Guid.TryParse(memoryId, out var parsedMemoryId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(memoryId)] = ["The personal memory identifier is invalid."],
            });
        }

        var deleted = await personalMemoryStore.DeleteOwnedAsync(
            parsedMemoryId,
            ownerPrincipalId!,
            cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static IResult? Authorize(
        HttpContext httpContext,
        string requiredScope,
        out string? ownerPrincipalId)
    {
        ownerPrincipalId = null;
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (!httpContext.User.HasClaim(
                AuthorizationClaimTypes.Scope,
                requiredScope))
        {
            return Results.Forbid();
        }

        ownerPrincipalId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(ownerPrincipalId)
            ? Results.Unauthorized()
            : null;
    }

    private static IResult? RequirePersistence(SqliteConversationStoreOptions options) =>
        options.Enabled
            ? null
            : Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Personal memory persistence is disabled.");

    private static IResult ValidationProblem(ArgumentException exception) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [exception.ParamName ?? "request"] = [exception.Message],
        });
}
