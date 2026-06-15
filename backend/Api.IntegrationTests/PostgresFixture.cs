using Api.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Api.IntegrationTests;

/// <summary>
/// xUnit collection fixture: starts one real Postgres container, applies migrations,
/// and yields a fresh <see cref="AppDbContext"/> per test so tests run against actual
/// Npgsql — not InMemory.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;
    private string _connectionString = null!;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:15-alpine")
            .WithDatabase("fuel_test")
            .WithUsername("fuel")
            .WithPassword("postgres")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        // Apply migrations so the schema matches what the app expects.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>Create a fresh <see cref="AppDbContext"/> pointing at the container.</summary>
    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Truncate every data table (keeping the schema + migrations history) so a test starts
    /// from a clean slate. Tests in the "Postgres" collection run sequentially, so calling
    /// this at the top of a test is safe and lets it assert on whole-table counts/ordering
    /// without cross-test pollution. Name-agnostic: it discovers tables from the catalog.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            DECLARE r RECORD;
            BEGIN
              FOR r IN (
                SELECT tablename FROM pg_tables
                WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory'
              )
              LOOP
                EXECUTE 'TRUNCATE TABLE ' || quote_ident(r.tablename) || ' RESTART IDENTITY CASCADE';
              END LOOP;
            END $$;
            """);
    }
}

/// <summary>
/// Shared collection so all integration tests reuse one container.
/// </summary>
[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
