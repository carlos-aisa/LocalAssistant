using System.Security.Claims;
using LocalAssistant.Core.Security.ToolRisk;

namespace LocalAssistant.Api.Security;

public sealed class HttpContextToolPolicyContextAccessor(
    IHttpContextAccessor httpContextAccessor) : IToolPolicyContextAccessor
{
    public ToolPolicyContext GetCurrent()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return ToolPolicyContext.Anonymous;
        }

        var principalId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return ToolPolicyContext.Anonymous;
        }

        var scopes = new HashSet<string>(
            principal.FindAll(AuthorizationClaimTypes.Scope)
                .Select(claim => claim.Value),
            StringComparer.Ordinal);
        return new ToolPolicyContext(principalId, scopes);
    }
}
