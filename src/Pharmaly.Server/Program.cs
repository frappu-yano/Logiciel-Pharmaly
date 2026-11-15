using System.Reflection;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Catalog;
using Pharmaly.Server.Database;

const string DemoDataArgument = "--donnees-fictives";
const string MigrateOnlyArgument = "--migrer";
const string InitializeSiteArgument = "--initialiser-site";
const string CreateRegisterAccountArgument = "--creer-compte-caisse";
const string CreateAdministratorArgument = "--creer-administrateur";

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "Pharmaly Gestion - Serveur");

// Réglages propres au site (mot de passe de la base) : fichier appsettings.Local.json,
// jamais versionné. Une valeur fournie par l'environnement ou par les tests l'emporte.
// La lecture se fait à la création du service, quand la configuration est complète.
builder.Services.AddSingleton(services =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Pharmaly")
        ?? new ConfigurationBuilder()
            .SetBasePath(services.GetRequiredService<IHostEnvironment>().ContentRootPath)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build()
            .GetConnectionString("Pharmaly")
        ?? throw new InvalidOperationException(
            "Chaîne de connexion absente : renseignez ConnectionStrings:Pharmaly dans appsettings.Local.json.");
    return NpgsqlDataSource.Create(connectionString);
});
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<ProductQueries>();
builder.Services.AddSingleton<DemoDataSeeder>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<SiteSetup>();

var app = builder.Build();

await app.Services.GetRequiredService<MigrationRunner>().ApplyAsync();

if (args.Contains(MigrateOnlyArgument))
{
    return;
}

var demoIndex = Array.IndexOf(args, DemoDataArgument);
if (demoIndex >= 0)
{
    var count = demoIndex + 1 < args.Length && int.TryParse(args[demoIndex + 1], out var parsed) ? parsed : 20_000;
    await app.Services.GetRequiredService<DemoDataSeeder>().SeedAsync(count);
    app.Logger.LogInformation("{Count} produits fictifs chargés.", count);
    return;
}

// Mise en place d'un site par la personne qui installe le serveur.
//   --initialiser-site CODE "Nom du site" MAGASIN|DEPOT
//   --creer-compte-caisse identifiant "Nom affiché" CODE_DU_SITE
//   --creer-administrateur MATRICULE "Nom complet"
// Les secrets générés s'affichent une seule fois, sur une ligne « SECRET=... ».
string[] ArgumentsAfter(string name, int count)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + count < args.Length ? args[(index + 1)..(index + 1 + count)] : [];
}

var setup = app.Services.GetRequiredService<SiteSetup>();
if (ArgumentsAfter(InitializeSiteArgument, 3) is [var siteCode, var siteName, var siteKind])
{
    await setup.InitializeSiteAsync(siteCode, siteName, siteKind);
    Console.WriteLine($"Site « {siteName} » créé.");
    return;
}

if (ArgumentsAfter(CreateRegisterAccountArgument, 3) is [var accountLogin, var accountName, var accountStore])
{
    Console.WriteLine($"SECRET={await setup.CreateRegisterAccountAsync(accountLogin, accountName, accountStore)}");
    return;
}

if (ArgumentsAfter(CreateAdministratorArgument, 2) is [var adminNumber, var adminName])
{
    Console.WriteLine($"SECRET={await setup.CreateAdministratorAsync(int.Parse(adminNumber), adminName)}");
    return;
}

if (args.Any(argument => argument is InitializeSiteArgument or CreateRegisterAccountArgument or CreateAdministratorArgument))
{
    Console.Error.WriteLine("Arguments incomplets. Voir README.md, section « Mise en place d'un site ».");
    Environment.ExitCode = 2;
    return;
}

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

app.MapGet(ApiRoutes.Health, async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var schemaVersion = await MigrationRunner.ReadSchemaVersionAsync(connection);
        return Results.Ok(new HealthResponse("OK", version, true, schemaVersion, DateTimeOffset.Now));
    }
    catch (NpgsqlException)
    {
        return Results.Json(
            new HealthResponse("BASE_INDISPONIBLE", version, false, 0, DateTimeOffset.Now),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet(ApiRoutes.ProductSearch, async (string? q, int? limite, ProductQueries queries, CancellationToken cancellationToken) =>
    Results.Ok(await queries.SearchAsync(q, limite ?? ProductQueries.DefaultLimit, cancellationToken)))
    .RequirePermission(Permissions.ProductsView);

app.MapGet(ApiRoutes.ProductByBarcode + "/{barcode}", async (string barcode, ProductQueries queries, CancellationToken cancellationToken) =>
    await queries.FindByBarcodeAsync(barcode, cancellationToken) is { } product
        ? Results.Ok(product)
        : Results.NotFound())
    .RequirePermission(Permissions.ProductsView);

app.MapAccessEndpoints();
app.MapEmployeeAdminEndpoints();
app.MapStoreEndpoints();

await app.RunAsync();

public partial class Program;
