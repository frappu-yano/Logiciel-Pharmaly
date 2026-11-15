using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Pharmaly.Tests.Server;

/// <summary>
/// Démarre le vrai serveur sur une base PostgreSQL neuve, créée pour la série de tests
/// et supprimée à la fin. La connexion d'administration vient de PHARMALY_TEST_POSTGRES.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private const string DefaultAdminConnection = "Host=localhost;Username=pharmaly;Password=pharmaly;Database=postgres";

    private readonly string adminConnection =
        Environment.GetEnvironmentVariable("PHARMALY_TEST_POSTGRES") ?? DefaultAdminConnection;

    private readonly string databaseName = $"pharmaly_test_{Guid.NewGuid():N}";
    private WebApplicationFactory<Program>? factory;

    public string ConnectionString { get; private set; } = string.Empty;

    public HttpClient Client { get; private set; } = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IServiceProvider Services => factory!.Services;

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsAdminAsync($"CREATE DATABASE {databaseName}");
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Pharmaly", ConnectionString));
        Client = factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsAdminAsync($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)");
    }

    private async Task ExecuteAsAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
