using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Stock;

/// <summary>Emplacements, ajustement d'inventaire et transfert simple entre emplacements.</summary>
public static class StockEndpoints
{
    public const string AdjustOperation = "STOCK_AJUSTER";
    public const string TransferOperation = "STOCK_TRANSFERER";
    public const string AdjustmentSource = "AJUSTEMENT";
    public const string TransferSource = "TRANSFERT";

    private const int MaxReasonLength = 500;
    private const int MaxTransferLines = 500;

    public static void MapStockEndpoints(this WebApplication app)
    {
        // Liste des emplacements : nécessaire à tout employé identifié (fiche produit, ajustement, transfert).
        app.MapGet(ApiRoutes.StockLocations, async (NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await connection.QueryAsync<StockLocationSummary>(
                """
                SELECT l.id AS Id, l.code AS Code, l.name AS Name, l.kind AS Kind, s.id AS StoreId, s.name AS StoreName
                FROM stock_locations l JOIN stores s ON s.id = l.store_id
                WHERE l.is_active AND s.is_active
                ORDER BY s.name, l.kind DESC, l.code
                """));
        }).RequireEmployee();

        app.MapPost(ApiRoutes.StockAdjustments, AdjustAsync).RequirePermission(Permissions.StockAdjust);
        app.MapPost(ApiRoutes.StockTransfers, TransferAsync).RequirePermission(Permissions.StockTransfer);
    }

    private static async Task<IResult> AdjustAsync(
        StockAdjustmentRequest request, HttpContext context, NpgsqlDataSource dataSource, OperationRunner runner, StockEngine engine)
    {
        var identity = context.GetIdentity();
        if (await RefuseOutsideGrantedStoresAsync(dataSource, context, Permissions.StockAdjust, request.LocationId) is { } refusal)
        {
            return refusal;
        }

        return await runner.RunAsync(request.OperationId, AdjustOperation, identity, request, async operation =>
        {
            var reason = (request.Reason ?? string.Empty).Trim();
            if (reason.Length is 0 or > MaxReasonLength)
            {
                throw BusinessException.Invalid($"Le motif de l'ajustement est obligatoire ({MaxReasonLength} caractères au plus).");
            }

            if (request.NewQuantity is null == request.QuantityChange is null)
            {
                throw BusinessException.Invalid("Indiquez soit la quantité comptée, soit la correction, pas les deux.");
            }

            var change = request.NewQuantity is { } counted
                ? StockChange.SetTo(request.ProductId, request.LocationId, counted, StockMovementTypes.Adjustment)
                : StockChange.Move(request.ProductId, request.LocationId, request.QuantityChange!.Value, StockMovementTypes.Adjustment);
            var applied = (await engine.ApplyAsync(
                operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId,
                [change with { SourceType = AdjustmentSource, SourceId = operation.OperationId.ToString(), Note = reason }]))[0];

            await operation.AuditAsync(
                AuditActions.StockAdjusted, "product", request.ProductId.ToString(),
                new
                {
                    emplacement = request.LocationId,
                    avant = applied.QuantityBefore,
                    ecart = applied.Quantity,
                    apres = applied.QuantityAfter,
                    motif = reason,
                });
            return new StockAdjustmentResult(
                request.ProductId, request.LocationId, applied.QuantityBefore, applied.Quantity, applied.QuantityAfter);
        });
    }

    private static async Task<IResult> TransferAsync(
        StockTransferRequest request, HttpContext context, NpgsqlDataSource dataSource, OperationRunner runner, StockEngine engine)
    {
        var identity = context.GetIdentity();
        if (await RefuseOutsideGrantedStoresAsync(
                dataSource, context, Permissions.StockTransfer, request.FromLocationId, request.ToLocationId) is { } refusal)
        {
            return refusal;
        }

        return await runner.RunAsync(request.OperationId, TransferOperation, identity, request, async operation =>
        {
            var lines = request.Lines ?? [];
            if (lines.Count is 0 or > MaxTransferLines)
            {
                throw BusinessException.Invalid($"Un transfert contient de 1 à {MaxTransferLines} lignes.");
            }

            if (request.FromLocationId == request.ToLocationId)
            {
                throw BusinessException.Invalid("L'emplacement de départ et celui d'arrivée doivent être différents.");
            }

            if (lines.Any(line => line.Quantity <= 0))
            {
                throw BusinessException.Invalid("Chaque quantité transférée doit être positive.");
            }

            if (lines.Select(line => line.ProductId).Distinct().Count() != lines.Count)
            {
                throw BusinessException.Invalid("Le même produit figure sur plusieurs lignes du transfert.");
            }

            var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            if (note?.Length > MaxReasonLength)
            {
                throw BusinessException.Invalid($"La note a {MaxReasonLength} caractères au plus.");
            }

            // Sortie puis entrée de chaque ligne, toutes dans le même appel : tout ou rien.
            var transferId = operation.OperationId.ToString();
            var changes = lines.SelectMany(line => new[]
            {
                StockChange.Move(line.ProductId, request.FromLocationId, -line.Quantity, StockMovementTypes.TransferOut)
                    with { SourceType = TransferSource, SourceId = transferId, Note = note },
                StockChange.Move(line.ProductId, request.ToLocationId, line.Quantity, StockMovementTypes.TransferIn)
                    with { SourceType = TransferSource, SourceId = transferId, Note = note },
            }).ToList();
            var applied = await engine.ApplyAsync(
                operation.Connection, operation.Transaction, operation.EmployeeId, operation.OperationId, changes);

            var resultLines = lines.Select((line, index) => new StockTransferResultLine(
                line.ProductId, line.Quantity, applied[2 * index].QuantityAfter, applied[2 * index + 1].QuantityAfter)).ToList();
            await operation.AuditAsync(
                AuditActions.StockTransferred, "stock_transfer", transferId,
                new
                {
                    de = request.FromLocationId,
                    vers = request.ToLocationId,
                    lignes = lines.Select(line => new { produit = line.ProductId, quantite = line.Quantity }),
                    note,
                });
            return new StockTransferResult(operation.OperationId, resultLines);
        });
    }

    /// <summary>
    /// Un droit peut être limité à un site : il doit couvrir le site de chaque emplacement
    /// touché, pas seulement celui du poste. Le refus est audité comme tout refus de droit.
    /// </summary>
    private static async Task<IResult?> RefuseOutsideGrantedStoresAsync(
        NpgsqlDataSource dataSource, HttpContext context, string permission, params Guid[] locationIds)
    {
        var identity = context.GetIdentity();
        await using var connection = await dataSource.OpenConnectionAsync();
        var uncovered = (await connection.QueryAsync<string>(
            """
            SELECT DISTINCT s.name
            FROM stock_locations l JOIN stores s ON s.id = l.store_id
            WHERE l.id = ANY(@locationIds)
              AND NOT EXISTS (SELECT 1 FROM employee_permissions ep
                              WHERE ep.employee_id = @employeeId AND ep.permission_code = @permission
                                AND (ep.store_id IS NULL OR ep.store_id = l.store_id))
            ORDER BY s.name
            """,
            new { locationIds, employeeId = identity.EmployeeId, permission })).AsList();
        if (uncovered.Count == 0)
        {
            return null;
        }

        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.PermissionDenied, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                Details: new { permission, chemin = context.Request.Path.Value, sites = uncovered }));
        return Results.Json(
            new ApiError(ApiErrorCodes.PermissionDenied, $"Vous n'avez pas ce droit sur le site « {uncovered[0]} »."),
            statusCode: StatusCodes.Status403Forbidden);
    }
}
