using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Access;

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

    /// <summary>Client sans aucun jeton.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Client d'un poste connecté, avec l'administrateur identifié.</summary>
    public HttpClient AdminClient { get; private set; } = null!;

    public const string RegisterLogin = "caisse1";
    public const int AdminNumber = 1;

    public string RegisterPassword { get; private set; } = string.Empty;

    public string AdminSecret { get; private set; } = string.Empty;

    public Guid AdminId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid OtherStoreId { get; private set; }

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IServiceProvider Services => factory!.Services;

    /// <summary>Horloge du serveur : l'heure réelle, décalable pour passer au lendemain.</summary>
    public AdjustableClock Clock { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await ExecuteAsAdminAsync($"CREATE DATABASE {databaseName}");
        // Réserve de connexions bornée : les tests de concurrence lancent des dizaines de demandes
        // à la fois et plusieurs séries tournent en parallèle sur le même PostgreSQL (100 connexions
        // au plus pour toutes les séries : au-delà, PostgreSQL refuse et les demandes échouent).
        ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName, MaxPoolSize = 16 }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Pharmaly", ConnectionString);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
        });
        Client = factory.CreateClient();

        var setup = Services.GetRequiredService<SiteSetup>();
        StoreId = await setup.InitializeSiteAsync("D1", "Pharmaly 1 — Didouche", "MAGASIN");
        OtherStoreId = await setup.InitializeSiteAsync("A2", "Pharmaly 2 — Audin", "MAGASIN");
        RegisterPassword = await setup.CreateRegisterAccountAsync(RegisterLogin, "Caisse 1", "D1");
        AdminSecret = await setup.CreateAdministratorAsync(AdminNumber, "Administratrice Test");
        await using var connection = await DataSource.OpenConnectionAsync();
        AdminId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM employees WHERE employee_number = @AdminNumber", new { AdminNumber });

        AdminClient = await CreatePosteClientAsync();
        await IdentifyAsync(AdminClient, AdminNumber, AdminSecret);
    }

    /// <summary>Nouveau poste connecté avec le compte de caisse, sans employé identifié.</summary>
    public Task<HttpClient> CreatePosteClientAsync() => CreatePosteClientAsync(RegisterLogin, RegisterPassword);

    /// <summary>Nouveau poste connecté avec un autre compte de caisse, sans employé identifié.</summary>
    public async Task<HttpClient> CreatePosteClientAsync(string login, string password)
    {
        var client = factory!.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.PosteSignIn, new PosteSignInRequest(login, password));
        response.EnsureSuccessStatusCode();
        var signedIn = (await response.Content.ReadFromJsonAsync<PosteSignInResponse>())!;
        client.DefaultRequestHeaders.Add(ApiHeaders.PosteToken, signedIn.Token);
        return client;
    }

    public static async Task<EmployeeSignInResponse> IdentifyAsync(HttpClient posteClient, int employeeNumber, string secret)
    {
        var response = await posteClient.PostAsJsonAsync(ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employeeNumber, secret));
        response.EnsureSuccessStatusCode();
        var signedIn = (await response.Content.ReadFromJsonAsync<EmployeeSignInResponse>())!;
        posteClient.DefaultRequestHeaders.Remove(ApiHeaders.EmployeeToken);
        posteClient.DefaultRequestHeaders.Add(ApiHeaders.EmployeeToken, signedIn.Token);
        return signedIn;
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

/// <summary>Heure réelle plus un décalage choisi par le test (zéro par défaut).</summary>
public sealed class AdjustableClock : TimeProvider
{
    private long offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));
        set => Interlocked.Exchange(ref offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
