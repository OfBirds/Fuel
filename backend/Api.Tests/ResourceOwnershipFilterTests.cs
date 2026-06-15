using Api.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;

namespace Api.Tests;

/// <summary>
/// Covers the cross-user access boundary: an authenticated caller may only act on their
/// own <c>userId</c> (route or query); a mismatch is 403; anonymous/no-userId actions pass through.
/// </summary>
public class ResourceOwnershipFilterTests
{
    private static ActionExecutingContext Context(ClaimsPrincipal user, string? routeUserId, string? queryUserId)
    {
        var http = new DefaultHttpContext { User = user };
        if (queryUserId is not null)
            http.Request.QueryString = new QueryString($"?userId={queryUserId}");

        var routeData = new RouteData();
        if (routeUserId is not null)
            routeData.Values["userId"] = routeUserId;

        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }

    private static ClaimsPrincipal AuthedAs(Guid userId) =>
        new(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "Test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static async Task<bool> Invoke(ActionExecutingContext ctx)
    {
        var nextCalled = false;
        await new ResourceOwnershipFilter().OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return nextCalled;
    }

    [Fact]
    public async Task MatchingRouteUserId_Proceeds()
    {
        var id = Guid.NewGuid();
        var ctx = Context(AuthedAs(id), id.ToString(), null);
        Assert.True(await Invoke(ctx));
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task MismatchedRouteUserId_Forbidden()
    {
        var ctx = Context(AuthedAs(Guid.NewGuid()), Guid.NewGuid().ToString(), null);
        Assert.False(await Invoke(ctx));
        Assert.IsType<ForbidResult>(ctx.Result);
    }

    [Fact]
    public async Task MismatchedQueryUserId_Forbidden()
    {
        var ctx = Context(AuthedAs(Guid.NewGuid()), null, Guid.NewGuid().ToString());
        Assert.False(await Invoke(ctx));
        Assert.IsType<ForbidResult>(ctx.Result);
    }

    [Fact]
    public async Task NoUserId_Proceeds()
    {
        var ctx = Context(AuthedAs(Guid.NewGuid()), null, null);
        Assert.True(await Invoke(ctx));
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task AnonymousCaller_IsNotEnforced()
    {
        // Anonymous endpoints (auth/version/unsubscribe) carry no principal — the filter
        // must not block them even if a userId is present in the route.
        var ctx = Context(Anonymous(), Guid.NewGuid().ToString(), null);
        Assert.True(await Invoke(ctx));
        Assert.Null(ctx.Result);
    }
}
