using System.Security.Claims;
using System.Text.Json;
using Api.Services;
using Microsoft.AspNetCore.Http;

namespace Api.Tests;

/// <summary>
/// EmailVerificationHoldMiddleware — a held login (the marker claim from <see cref="OidcUserProvisioner"/>)
/// is short-circuited with a clear 403 and never forwarded; everything else passes through. This is the
/// "tell the user what's wrong" guarantee: an unverified email matching an existing account gets a
/// machine-readable "verify your email" response instead of an opaque failure.
/// </summary>
public class EmailVerificationHoldMiddlewareTests
{
    private static DefaultHttpContext ContextWith(params Claim[] claims)
    {
        var ctx = new DefaultHttpContext();
        // authenticationType non-null ⇒ IsAuthenticated true; null (no claims) ⇒ anonymous.
        ctx.User = new ClaimsPrincipal(
            new ClaimsIdentity(claims, authenticationType: claims.Length > 0 ? "Bearer" : null));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static EmailVerificationHoldMiddleware Middleware(Action onNext) =>
        new(_ => { onNext(); return Task.CompletedTask; });

    [Fact]
    public async Task HeldLogin_Gets403WithMessage_AndIsNotForwarded()
    {
        var ctx = ContextWith(
            new Claim("sub", "12345"),
            new Claim(OidcUserProvisioner.HoldClaimType, OidcUserProvisioner.EmailUnverifiedHold));
        var forwarded = false;

        await Middleware(() => forwarded = true).InvokeAsync(ctx);

        Assert.False(forwarded);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        Assert.Equal("email_unverified", doc.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task MappedLogin_IsForwarded()
    {
        var ctx = ContextWith(new Claim("sub", Guid.NewGuid().ToString()));
        var forwarded = false;

        await Middleware(() => forwarded = true).InvokeAsync(ctx);

        Assert.True(forwarded);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequest_IsForwarded()
    {
        var ctx = ContextWith();
        var forwarded = false;

        await Middleware(() => forwarded = true).InvokeAsync(ctx);

        Assert.True(forwarded);
    }
}
