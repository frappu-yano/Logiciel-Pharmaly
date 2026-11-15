using Dapper;
using Npgsql;
using Pharmaly.Contracts;

namespace Pharmaly.Server.Access;

public static class StoreEndpoints
{
    public static void MapStoreEndpoints(this WebApplication app)
    {
        // Liste des sites : nécessaire à tout employé identifié (choix d'un site pour un droit, un transfert…).
        app.MapGet(ApiRoutes.Stores, async (NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await connection.QueryAsync<StoreSummary>(
                "SELECT id AS Id, code AS Code, name AS Name, kind AS Kind FROM stores WHERE is_active ORDER BY name"));
        }).RequireEmployee();
    }
}
