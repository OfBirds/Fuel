using System.Net;
using System.Reflection;
using System.Text.Json;
using Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Api.Tests;

/// <summary>
/// Covers the CrimsonRaven reachability probe behind GET /api/config — in particular the
/// cancellation-vs-timeout split: a browser aborting /api/config mid-probe must NOT be
/// recorded as "CrimsonRaven offline" (it says nothing about the IdP), while an HttpClient
/// timeout (also an OperationCanceledException, but with a different token) must be.
/// No real HTTP — stub handlers; the controller's static probe cache is reset per test.
/// </summary>
public class ConfigControllerTests
{
    public ConfigControllerTests() => ResetProbeCache();

    /// <summary>The probe verdict is cached in statics; start each test from "never probed".</summary>
    private static void ResetProbeCache()
    {
        typeof(ConfigController)
            .GetField("_checkedUtc", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, DateTime.MinValue);
        typeof(ConfigController)
            .GetField("_online", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, false);
    }

    private static ConfigController CreateController(
        HttpMessageHandler handler,
        string? authority = "https://raven.test/realms/crimsonraven",
        string? authMode = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OIDC_AUTHORITY"] = authority,
            ["OIDC_CLIENT_ID"] = "fuel",
            ["AUTH_MODE"] = authMode,
        }).Build();
        return new ConfigController(cfg, new FreshClientFactory(handler));
    }

    private static async Task<JsonElement> GetPayload(ConfigController controller, CancellationToken ct = default)
    {
        var ok = Assert.IsType<OkObjectResult>(await controller.Get(ct));
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Fact]
    public async Task Get_NoAuthority_DisabledAndNeverProbes()
    {
        var handler = new ExplodingHandler(); // any probe attempt fails the test
        var payload = await GetPayload(CreateController(handler, authority: null));

        Assert.False(payload.GetProperty("oidcEnabled").GetBoolean());
        Assert.False(payload.GetProperty("oidcOnline").GetBoolean());
        Assert.Equal("crimsonraven", payload.GetProperty("authMode").GetString());
    }

    [Fact]
    public async Task Get_AuthModeLegacy_Reported()
    {
        var payload = await GetPayload(CreateController(new StubHandler(HttpStatusCode.OK), authMode: "legacy"));
        Assert.Equal("legacy", payload.GetProperty("authMode").GetString());
    }

    [Fact]
    public async Task Get_DiscoveryReachable_ReportsOnline()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var payload = await GetPayload(CreateController(handler));

        Assert.True(payload.GetProperty("oidcOnline").GetBoolean());
        Assert.Equal("https://raven.test/realms/crimsonraven/.well-known/openid-configuration",
            handler.LastUrl);
    }

    [Fact]
    public async Task Get_DiscoveryNonSuccess_ReportsOffline()
    {
        var payload = await GetPayload(CreateController(new StubHandler(HttpStatusCode.BadGateway)));
        Assert.False(payload.GetProperty("oidcOnline").GetBoolean());
    }

    [Fact]
    public async Task Get_ConnectionFailure_ReportsOffline()
    {
        var payload = await GetPayload(CreateController(new ThrowingHandler(new HttpRequestException("no route"))));
        Assert.False(payload.GetProperty("oidcOnline").GetBoolean());
    }

    /// <summary>A positive verdict is cached: a later probe failure within the TTL is not seen.</summary>
    [Fact]
    public async Task Get_OnlineVerdict_IsCached()
    {
        var first = await GetPayload(CreateController(new StubHandler(HttpStatusCode.OK)));
        Assert.True(first.GetProperty("oidcOnline").GetBoolean());

        // Raven "goes down", but the cached positive is still fresh → still reported online.
        var second = await GetPayload(CreateController(new ExplodingHandler()));
        Assert.True(second.GetProperty("oidcOnline").GetBoolean());
    }

    /// <summary>
    /// An HttpClient timeout is an OperationCanceledException whose token is NOT the request's —
    /// it must be recorded (and cached) as offline, unlike a client abort.
    /// </summary>
    [Fact]
    public async Task Get_ProbeTimeout_RecordsAndCachesOffline()
    {
        var timedOut = await GetPayload(CreateController(
            new ThrowingHandler(new TaskCanceledException("simulated HttpClient timeout"))));
        Assert.False(timedOut.GetProperty("oidcOnline").GetBoolean());

        // The negative verdict was stamped: an immediately-following request within the
        // offline TTL serves the cache and never re-probes (handler would report online).
        var cached = await GetPayload(CreateController(new StubHandler(HttpStatusCode.OK)));
        Assert.False(cached.GetProperty("oidcOnline").GetBoolean());
    }

    /// <summary>
    /// Regression test for the false-offline bug: a browser aborting /api/config mid-probe
    /// used to be swallowed by a bare catch and cached as "CrimsonRaven offline" for the TTL,
    /// showing every user the break-glass banner while the IdP was healthy.
    /// </summary>
    [Fact]
    public async Task Get_ClientAbortsMidProbe_DoesNotPoisonCache()
    {
        using var cts = new CancellationTokenSource();
        var aborting = CreateController(new AbortHandler(cts));

        // The cancellation propagates out (the request is dead anyway)…
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborting.Get(cts.Token));

        // …and no verdict was stamped: the very next request re-probes and sees Raven up.
        // (With the old bug this served a cached "offline" instead.)
        var next = await GetPayload(CreateController(new StubHandler(HttpStatusCode.OK)));
        Assert.True(next.GetProperty("oidcOnline").GetBoolean());
    }

    // ── Test doubles ──

    /// <summary>Returns a fixed status; records the last URL probed.</summary>
    private class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw exception;
    }

    /// <summary>Simulates the browser dropping the request mid-probe: cancels the request's
    /// own token, then throws the cancellation — the shape a real client abort has.</summary>
    private class AbortHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
    }

    /// <summary>Fails the test if any probe is attempted.</summary>
    private class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("probe was not expected");
    }

    /// <summary>A fresh HttpClient per CreateClient(): the controller sets Timeout on it,
    /// which HttpClient forbids after first use — sharing one instance would throw.</summary>
    private class FreshClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
