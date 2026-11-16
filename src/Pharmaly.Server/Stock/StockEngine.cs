using System.Globalization;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Stock;

/// <summary>
/// Variation demandée au moteur de stock : soit un mouvement signé (<see cref="Move"/>),
/// soit une quantité à atteindre (<see cref="SetTo"/>, pour un inventaire).
/// </summary>
public sealed record StockChange
{
    private StockChange()
    {
    }

    public required Guid ProductId { get; init; }

    public required Guid LocationId { get; init; }

    public required string Type { get; init; }

    /// <summary>Quantité signée : positive pour une entrée, négative pour une sortie.</summary>
    public decimal? Quantity { get; private init; }

    /// <summary>Quantité à atteindre ; l'écart avec le solde est calculé sous verrou.</summary>
    public decimal? TargetQuantity { get; private init; }

    public string? SourceType { get; init; }

    public string? SourceId { get; init; }

    public string? Note { get; init; }

    public static StockChange Move(Guid productId, Guid locationId, decimal quantity, string type) =>
        new() { ProductId = productId, LocationId = locationId, Type = type, Quantity = quantity };

    public static StockChange SetTo(Guid productId, Guid locationId, decimal targetQuantity, string type) =>
        new() { ProductId = productId, LocationId = locationId, Type = type, TargetQuantity = targetQuantity };
}

/// <summary>Effet d'une variation. Quantity vaut 0 quand le solde était déjà la quantité visée.</summary>
public sealed record AppliedStockChange(Guid ProductId, Guid LocationId, decimal QuantityBefore, decimal Quantity, decimal QuantityAfter);

public sealed class InsufficientStockException(string productName, decimal requested, decimal available)
    : BusinessException(
        ApiErrorCodes.InsufficientStock,
        $"Stock insuffisant pour « {productName} » : {StockEngine.Format(requested)} demandé, {StockEngine.Format(available)} disponible.",
        StatusCodes.Status409Conflict)
{
    public string ProductName { get; } = productName;

    public decimal Requested { get; } = requested;

    public decimal Available { get; } = available;
}

/// <summary>
/// Unique point de passage de TOUTE variation de stock.
/// <para>
/// À appeler UNE fois par transaction, avec tous les mouvements de l'opération : les soldes
/// concernés sont verrouillés par identifiant de produit croissant (puis d'emplacement),
/// ce qui met les opérations concurrentes en file sans interblocage. Le solde et le
/// journal sont écrits dans la transaction reçue ; rien n'est validé ici.
/// </para>
/// </summary>
public sealed class StockEngine
{
    /// <summary>Les quantités sont stockées en numeric(12,3).</summary>
    private const int MaxDecimals = 3;
    private const decimal MaxQuantity = 999_999_999.999m;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string Format(decimal quantity) => quantity.ToString("0.###", French);

    public async Task<IReadOnlyList<AppliedStockChange>> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid employeeId,
        Guid operationId,
        IReadOnlyList<StockChange> changes)
    {
        if (changes.Count == 0)
        {
            return [];
        }

        foreach (var change in changes)
        {
            Validate(change);
        }

        var productIds = changes.Select(change => change.ProductId).Distinct().ToArray();
        var products = (await connection.QueryAsync<(Guid Id, string Name, bool SoldByUnit)>(
            "SELECT id, name, sold_by_unit FROM products WHERE id = ANY(@productIds)", new { productIds }, transaction))
            .ToDictionary(product => product.Id);
        if (products.Count != productIds.Length)
        {
            throw BusinessException.NotFound("Produit introuvable.");
        }

        foreach (var change in changes)
        {
            var product = products[change.ProductId];
            var quantity = change.Quantity ?? change.TargetQuantity!.Value;
            if (product.SoldByUnit && quantity != decimal.Truncate(quantity))
            {
                throw new BusinessException(
                    ApiErrorCodes.FractionalQuantity,
                    $"« {product.Name} » se vend à l'unité : la quantité {Format(quantity)} n'est pas entière.");
            }
        }

        var locationIds = changes.Select(change => change.LocationId).Distinct().ToArray();
        var knownLocations = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM stock_locations WHERE id = ANY(@locationIds)", new { locationIds }, transaction);
        if (knownLocations != locationIds.Length)
        {
            throw BusinessException.NotFound("Emplacement de stock introuvable.");
        }

        var keys = changes.Select(change => (change.ProductId, change.LocationId)).Distinct().ToArray();
        var keyParameters = new
        {
            products = keys.Select(key => key.ProductId).ToArray(),
            locations = keys.Select(key => key.LocationId).ToArray(),
        };

        // 1. Les soldes absents sont créés à 0. Si une autre transaction crée le même au même
        //    moment, ON CONFLICT attend sa fin : pas de doublon, pas d'erreur.
        await connection.ExecuteAsync(
            """
            INSERT INTO stock_levels (product_id, location_id)
            SELECT k.product_id, k.location_id
            FROM unnest(@products, @locations) AS k (product_id, location_id)
            ORDER BY k.product_id, k.location_id
            ON CONFLICT (product_id, location_id) DO NOTHING
            """,
            keyParameters,
            transaction);

        // 2. Verrouillage dans un ordre unique : produit croissant, puis emplacement.
        var balances = (await connection.QueryAsync<(Guid ProductId, Guid LocationId, decimal Quantity)>(
            """
            SELECT sl.product_id, sl.location_id, sl.quantity
            FROM stock_levels sl
            WHERE (sl.product_id, sl.location_id) IN (SELECT * FROM unnest(@products, @locations))
            ORDER BY sl.product_id, sl.location_id
            FOR UPDATE OF sl
            """,
            keyParameters,
            transaction)).ToDictionary(row => (row.ProductId, row.LocationId), row => row.Quantity);

        // 3. Application dans l'ordre demandé, sans jamais passer sous zéro.
        var applied = new List<AppliedStockChange>(changes.Count);
        var journal = new List<(StockChange Change, decimal Quantity, decimal BalanceAfter)>(changes.Count);
        foreach (var change in changes)
        {
            var key = (change.ProductId, change.LocationId);
            var before = balances[key];
            var quantity = change.Quantity ?? change.TargetQuantity!.Value - before;
            var after = before + quantity;
            if (after < 0)
            {
                throw new InsufficientStockException(products[change.ProductId].Name, -quantity, before);
            }

            if (after > MaxQuantity)
            {
                throw BusinessException.Invalid("Quantité trop grande.");
            }

            balances[key] = after;
            applied.Add(new AppliedStockChange(change.ProductId, change.LocationId, before, quantity, after));
            if (quantity != 0)
            {
                journal.Add((change, quantity, after));
            }
        }

        if (journal.Count == 0)
        {
            return applied;
        }

        // 4. Soldes et journal, dans la même transaction.
        await connection.ExecuteAsync(
            """
            UPDATE stock_levels sl
            SET quantity = v.quantity, updated_at = now()
            FROM unnest(@products, @locations, @quantities) AS v (product_id, location_id, quantity)
            WHERE sl.product_id = v.product_id AND sl.location_id = v.location_id
            """,
            new { keyParameters.products, keyParameters.locations, quantities = keys.Select(key => balances[key]).ToArray() },
            transaction);

        await connection.ExecuteAsync(
            """
            INSERT INTO stock_movements
                (product_id, location_id, quantity, movement_type, source_type, source_id, note, balance_after,
                 employee_id, operation_id)
            SELECT m.product_id, m.location_id, m.quantity, m.movement_type, m.source_type, m.source_id, m.note,
                   m.balance_after, @employeeId, @operationId
            FROM unnest(@products, @locations, @quantities, @types, @sourceTypes, @sourceIds, @notes, @balances)
                 WITH ORDINALITY
                 AS m (product_id, location_id, quantity, movement_type, source_type, source_id, note, balance_after, position)
            ORDER BY m.position
            """,
            new
            {
                employeeId,
                operationId,
                products = journal.Select(line => line.Change.ProductId).ToArray(),
                locations = journal.Select(line => line.Change.LocationId).ToArray(),
                quantities = journal.Select(line => line.Quantity).ToArray(),
                types = journal.Select(line => line.Change.Type).ToArray(),
                sourceTypes = journal.Select(line => line.Change.SourceType).ToArray(),
                sourceIds = journal.Select(line => line.Change.SourceId).ToArray(),
                notes = journal.Select(line => line.Change.Note).ToArray(),
                balances = journal.Select(line => line.BalanceAfter).ToArray(),
            },
            transaction);

        return applied;
    }

    private static void Validate(StockChange change)
    {
        if (!StockMovementTypes.IsKnown(change.Type))
        {
            throw new ArgumentException($"Type de mouvement inconnu : {change.Type}.", nameof(change));
        }

        var quantity = change.Quantity ?? change.TargetQuantity!.Value;
        if (decimal.Round(quantity, MaxDecimals) != quantity)
        {
            throw BusinessException.Invalid($"Une quantité a au plus {MaxDecimals} décimales.");
        }

        if (Math.Abs(quantity) > MaxQuantity)
        {
            throw BusinessException.Invalid("Quantité trop grande.");
        }

        if (change.Quantity == 0)
        {
            throw BusinessException.Invalid("La quantité d'un mouvement ne peut pas être nulle.");
        }

        if (change.TargetQuantity < 0)
        {
            throw BusinessException.Invalid("Une quantité en stock ne peut pas être négative.");
        }
    }
}
