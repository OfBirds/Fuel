using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Api.Authorization;

/// <summary>
/// Enforces that an authenticated caller may only act on their own user resources.
/// Routes are shaped <c>api/user/{userId}/...</c> (and a few actions take a <c>userId</c>
/// query value); a valid token for user A must not be usable to read or mutate user B's
/// data. When the action carries a <c>userId</c> and it doesn't match the token's
/// <c>sub</c>, the request is rejected with 403.
/// </summary>
/// <remarks>
/// Registered globally. Anonymous endpoints (auth, version, unsubscribe) have no
/// authenticated principal, so the check is skipped and those routes stay open.
/// Actions without any <c>userId</c> (e.g. catalogue browse) are unaffected.
/// </remarks>
public class ResourceOwnershipFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var principal = context.HttpContext.User;

        if (principal.Identity?.IsAuthenticated == true
            && TryGetRequestedUserId(context, out var requestedUserId))
        {
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(sub, out var authenticatedUserId) || authenticatedUserId != requestedUserId)
            {
                context.Result = new Microsoft.AspNetCore.Mvc.ForbidResult();
                return;
            }
        }

        await next();
    }

    /// <summary>Pulls a <c>userId</c> from the route first, then the query string.</summary>
    private static bool TryGetRequestedUserId(ActionExecutingContext context, out Guid userId)
    {
        if (context.RouteData.Values.TryGetValue("userId", out var routeValue)
            && Guid.TryParse(routeValue?.ToString(), out userId))
            return true;

        var queryValue = context.HttpContext.Request.Query["userId"].ToString();
        return Guid.TryParse(queryValue, out userId);
    }
}
