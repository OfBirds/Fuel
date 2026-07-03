using Api.Services;
using WireMock.Net;
using WireMock.Server;

namespace Api.IntegrationTests;

/// <summary>
/// xUnit collection fixture: starts a WireMock.Net server on a random port
/// so estimator tests exercise the real HTTP stack against a stub AI provider.
/// </summary>
public class WireMockFixture : IAsyncLifetime
{
    public WireMockServer Server { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Server = WireMockServer.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Server.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Convenience: a connection pointing at this WireMock server.</summary>
    public ProviderConnection Connection(string model = "test-model", bool supportsImages = true, string? apiKey = null) =>
        new(Server.Url!, apiKey, model, supportsImages);
}

[CollectionDefinition("WireMock")]
public class WireMockCollection : ICollectionFixture<WireMockFixture>
{
}
