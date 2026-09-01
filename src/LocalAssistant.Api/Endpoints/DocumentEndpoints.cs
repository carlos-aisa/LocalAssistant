using LocalAssistant.Api.Contracts;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Documents;

namespace LocalAssistant.Api.Endpoints;

public static class DocumentEndpoints
{
    private const string SearchScope = "documents.search";
    private const string ReadScope = "documents.read";
    private const string ContentSearchScope = "documents.content.search";

    public static IEndpointRouteBuilder MapDocumentEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/documents", SearchAsync)
            .WithName("SearchDocuments")
            .WithSummary("Searches the authorized local documents root by metadata only.");
        endpoints.MapGet("/api/documents/{id}/content", ReadContentAsync)
            .WithName("ReadDocumentContent")
            .WithSummary("Reads bounded text content from an authorized local document.");
        endpoints.MapGet("/api/documents/content-search", SearchContentAsync)
            .WithName("SearchDocumentContent")
            .WithSummary("Searches authorized local text documents with a bounded matching excerpt.");

        return endpoints;
    }

    private static async Task<IResult> SearchContentAsync(
        [AsParameters] SearchDocumentContentRequest request,
        HttpContext httpContext,
        ILocalDocumentContentSearch documentContentSearch,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (!httpContext.User.HasClaim(
                AuthorizationClaimTypes.Scope,
                ContentSearchScope))
        {
            return Results.Forbid();
        }

        DocumentContentSearchQuery query;
        try
        {
            query = new DocumentContentSearchQuery(
                request.Text,
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

        var results = await documentContentSearch.SearchAsync(query, cancellationToken);
        if (!httpContext.User.HasClaim(
                AuthorizationClaimTypes.Scope,
                ReadScope))
        {
            results = results
                .Select(result => result with { Excerpt = null })
                .ToArray();
        }

        return Results.Ok(DocumentSearchResponse.FromResults(results));
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
                AuthorizationClaimTypes.Scope,
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

    private static async Task<IResult> ReadContentAsync(
        string id,
        HttpContext httpContext,
        ILocalDocumentContentReader documentContentReader,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (!httpContext.User.HasClaim(
                AuthorizationClaimTypes.Scope,
                ReadScope))
        {
            return Results.Forbid();
        }

        var outcome = await documentContentReader.ReadAsync(id, cancellationToken);
        if (outcome.Document is not null)
        {
            return Results.Ok(DocumentContentResponse.FromDocument(outcome.Document));
        }

        return outcome.Failure switch
        {
            DocumentContentReadFailure.UnsupportedFormat => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The document format is not supported."),
            DocumentContentReadFailure.TooLarge => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The document exceeds the maximum supported size."),
            _ => Results.NotFound(),
        };
    }
}
