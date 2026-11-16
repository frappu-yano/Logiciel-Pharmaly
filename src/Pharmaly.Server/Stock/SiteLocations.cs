using System.Data;
using Dapper;
using Pharmaly.Domain;
using Pharmaly.Server.Access;

namespace Pharmaly.Server.Stock;

/// <summary>Emplacements par défaut d'un site.</summary>
public static class SiteLocations
{
    /// <summary>
    /// Emplacement vendable du site : l'emplacement principal s'il existe, sinon le premier
    /// emplacement vendable actif. Null si le site n'en a aucun.
    /// </summary>
    public static Task<Guid?> ReadSellableAsync(IDbConnection connection, Guid storeId, IDbTransaction? transaction = null) =>
        connection.ExecuteScalarAsync<Guid?>(
            """
            SELECT id FROM stock_locations
            WHERE store_id = @storeId AND kind = @kind AND is_active
            ORDER BY (code = @mainCode) DESC, code
            LIMIT 1
            """,
            new { storeId, kind = StockLocationKinds.Sellable, mainCode = SiteSetup.MainLocationCode },
            transaction);
}
