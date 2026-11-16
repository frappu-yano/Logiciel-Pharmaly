using Dapper;
using Npgsql;
using Pharmaly.Domain;

namespace Pharmaly.Server.Catalog;

/// <summary>
/// Donne un stock de départ aux produits fictifs, pour les essais. Uniquement sur une base
/// d'essai : refusé si la base contient déjà le moindre mouvement de stock. Chaque quantité
/// passe par le journal (mouvement AJUSTEMENT), comme toute variation de stock.
/// </summary>
public sealed class DemoStockSeeder(NpgsqlDataSource dataSource)
{
    public async Task<int> SeedAsync(string storeCode, decimal quantity, int employeeNumber)
    {
        if (quantity <= 0 || quantity != decimal.Truncate(quantity))
        {
            throw new ArgumentException("La quantité doit être un nombre entier positif.");
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM stock_movements)", transaction: transaction))
        {
            throw new InvalidOperationException(
                "La base contient déjà des mouvements de stock : le stock fictif ne se charge que sur une base d'essai neuve.");
        }

        var locationId = await connection.ExecuteScalarAsync<Guid?>(
            """
            SELECT l.id FROM stock_locations l JOIN stores s ON s.id = l.store_id
            WHERE s.code = @code AND l.kind = @kind AND l.is_active
            ORDER BY (l.code = 'PRINCIPAL') DESC, l.code LIMIT 1
            """,
            new { code = storeCode.ToUpperInvariant(), kind = StockLocationKinds.Sellable },
            transaction)
            ?? throw new InvalidOperationException($"Aucun emplacement vendable pour le site « {storeCode} ».");

        var employeeId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM employees WHERE employee_number = @employeeNumber", new { employeeNumber }, transaction)
            ?? throw new InvalidOperationException($"Aucun employé ne porte le matricule {employeeNumber}.");

        var operationId = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO operations (operation_id, kind, employee_id, request_hash, result)
            VALUES (@operationId, 'STOCK_FICTIF', @employeeId, 'stock-fictif', '{}'::jsonb)
            """,
            new { operationId, employeeId },
            transaction);

        await connection.ExecuteAsync(
            """
            INSERT INTO stock_levels (product_id, location_id, quantity)
            SELECT id, @locationId, @quantity FROM products WHERE is_active
            """,
            new { locationId, quantity },
            transaction);

        var count = await connection.ExecuteAsync(
            """
            INSERT INTO stock_movements
                (product_id, location_id, quantity, movement_type, source_type, source_id, employee_id, operation_id, note, balance_after)
            SELECT id, @locationId, @quantity, @type, 'ESSAI', @source, @employeeId, @operationId, 'Stock fictif pour les essais', @quantity
            FROM products WHERE is_active
            """,
            new { locationId, quantity, type = StockMovementTypes.Adjustment, source = operationId.ToString(), employeeId, operationId },
            transaction);

        await transaction.CommitAsync();
        return count;
    }
}
