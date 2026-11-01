using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Tests.Integration;

// Real Postgres, not InMemory: constraints, jsonb and partial indexes only
// exist in the real database. Each test run gets its own throwaway database
// (created here, dropped in DisposeAsync), so the dev database is never touched.
//
// Needs the compose database running: `docker compose up -d`.
public sealed class PostgresApiFixture : IAsyncLifetime
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=postgres;Username=webhook;Password=webhook_dev_pw";

    private readonly string _databaseName = $"webhook_test_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = "";
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = _databaseName,
            MaxPoolSize = 50,
        }.ConnectionString;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Default", ConnectionString)
        );

        // Apply the real migration, exactly as production would.
        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext NewDbContext()
    {
        var scope = Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)",
            admin
        );
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "postgres";
}
