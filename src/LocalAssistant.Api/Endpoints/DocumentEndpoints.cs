using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Documents;

namespace LocalAssistant.Api.Endpoints;

public static class DocumentEndpoints
{
    private const string SearchScope = "documents.search";

    public static IEndpointRouteBuilder MapDocumentEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/documents", SearchAsync)
            .WithName("SearchDocuments")
            .WithSummary("Searches the authorized local documents root by metadata only.");

        return endpoints;
    }

    private static async Task<IResult> SearchAsync(
        [AsParameters] SearchDocumentsRequest request,
        HttpContext httpContext,
        ILocalDocumentSearch documentSearch,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (!httpContext.User.HasClaim(
                LocalApiKeyAuthenticationDefaults.ScopeClaimType,
                SearchScope))
        {
            return Results.Forbid();
        }

        DocumentSearchQuery query;
        try
        {
            query = new DocumentSearchQuery(
                request.Name,
                request.Extension,
                request.RelativePath,
                request.ModifiedAfterUtc,
                request.ModifiedBeforeUtc,
                request.Limit ?? DocumentSearchQuery.DefaultLimit);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [exception.ParamName ?? "query"] = [exception.Message],
            });
        }

        var results = await documentSearch.SearchAsync(query, cancellationToken);
        return Results.Ok(DocumentSearchResponse.FromResults(results));
    }
}
