using System.Globalization;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Stock : emplacements, ajustements et transferts, avec les refus et les messages du vrai serveur.</summary>
internal sealed partial class FakeServer : IStockClient
{
    private const string SiteName = "Pharmaly 1 — Didouche";
    private static readonly Guid SiteId = Guid.NewGuid();

    public static readonly StockLocationSummary MainLocation =
        new(Guid.NewGuid(), "PRINCIPAL", "Stock principal", StockLocationKinds.Sellable, SiteId, SiteName);

    public static readonly StockLocationSummary SetAsideLocation =
        new(Guid.NewGuid(), "ECARTE", "Produits mis à l'écart", StockLocationKinds.SetAside, SiteId, SiteName);

    public static readonly IReadOnlyList<StockLocationSummary> Locations = [MainLocation, SetAsideLocation];

    public List<StockAdjustmentRequest> Adjustments { get; } = [];

    public List<StockTransferRequest> Transfers { get; } = [];

    public Task<IReadOnlyList<StockLocationSummary>> ListLocationsAsync(CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult(Locations);
    }

    public Task<StockAdjustmentResult> AdjustAsync(StockAdjustmentRequest request, CancellationToken cancellationToken)
    {
        Adjustments.Add(request);
        EnsurePermission(Permissions.StockAdjust);
        return RunOperationAsync(request.OperationId, request with { OperationId = Guid.Empty }, () =>
        {
            if (request.Reason.Trim().Length == 0)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, "Le motif de l'ajustement est obligatoire (500 caractères au plus).");
            }

            if (request.NewQuantity is null == request.QuantityChange is null)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, "Indiquez soit la quantité comptée, soit la correction, pas les deux.");
            }

            var product = Find(request.ProductId);
            var before = product.Stock.GetValueOrDefault(request.LocationId);
            var change = request.NewQuantity is { } counted ? counted - before : request.QuantityChange!.Value;
            EnsureWholeIfSoldByUnit(product, request.NewQuantity ?? change);
            Move(product, request.LocationId, change, StockMovementTypes.Adjustment, request.Reason.Trim());
            return new StockAdjustmentResult(request.ProductId, request.LocationId, before, change, before + change);
        });
    }

    public Task<StockTransferResult> TransferAsync(StockTransferRequest request, CancellationToken cancellationToken)
    {
        Transfers.Add(request);
        EnsurePermission(Permissions.StockTransfer);
        return RunOperationAsync(request.OperationId, request with { OperationId = Guid.Empty }, () =>
        {
            if (request.Lines.Count == 0)
            {
                throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Un transfert contient de 1 à 500 lignes.");
            }

            if (request.FromLocationId == request.ToLocationId)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.InvalidRequest, "L'emplacement de départ et celui d'arrivée doivent être différents.");
            }

            // Tout ou rien : chaque ligne est contrôlée avant le premier mouvement.
            foreach (var line in request.Lines)
            {
                var product = Find(line.ProductId);
                EnsureWholeIfSoldByUnit(product, line.Quantity);
                var available = product.Stock.GetValueOrDefault(request.FromLocationId);
                if (available < line.Quantity)
                {
                    throw new ServerRefusedException(
                        ApiErrorCodes.InsufficientStock,
                        $"Stock insuffisant pour « {product.Name} » : {Format(line.Quantity)} demandé, {Format(available)} disponible.");
                }
            }

            var lines = request.Lines.Select(line =>
            {
                var product = Find(line.ProductId);
                Move(product, request.FromLocationId, -line.Quantity, StockMovementTypes.TransferOut, request.Note);
                Move(product, request.ToLocationId, line.Quantity, StockMovementTypes.TransferIn, request.Note);
                return new StockTransferResultLine(
                    line.ProductId, line.Quantity, product.Stock[request.FromLocationId], product.Stock[request.ToLocationId]);
            }).ToList();
            return new StockTransferResult(request.OperationId, lines);
        });
    }

    private static string Format(decimal quantity) => quantity.ToString("0.###", CultureInfo.GetCultureInfo("fr-FR"));

    private static void EnsureWholeIfSoldByUnit(FakeProduct product, decimal quantity)
    {
        if (product.SoldByUnit && quantity != decimal.Truncate(quantity))
        {
            throw new ServerRefusedException(
                ApiErrorCodes.FractionalQuantity,
                $"« {product.Name} » se vend à l'unité : la quantité {Format(quantity)} n'est pas entière.");
        }
    }

    private void Move(FakeProduct product, Guid locationId, decimal change, string type, string? note)
    {
        var after = product.Stock.GetValueOrDefault(locationId) + change;
        if (after < 0)
        {
            throw new ServerRefusedException(
                ApiErrorCodes.InsufficientStock,
                $"Stock insuffisant pour « {product.Name} » : {Format(-change)} demandé, {Format(after - change)} disponible.");
        }

        product.Stock[locationId] = after;
        product.Movements.Add(new StockMovementLine(
            nextMovementId++, DateTimeOffset.Now, locationId, Locations.First(location => location.Id == locationId).Name, type,
            change, after, EmployeeName, null, null, note));
    }
}
