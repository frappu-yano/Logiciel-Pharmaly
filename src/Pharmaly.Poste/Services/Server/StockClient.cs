using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « stock » : emplacements, ajustement d'inventaire et transfert simple.</summary>
public interface IStockClient
{
    /// <summary>Emplacements actifs de tous les sites.</summary>
    Task<IReadOnlyList<StockLocationSummary>> ListLocationsAsync(CancellationToken cancellationToken);

    Task<StockAdjustmentResult> AdjustAsync(StockAdjustmentRequest request, CancellationToken cancellationToken);

    Task<StockTransferResult> TransferAsync(StockTransferRequest request, CancellationToken cancellationToken);
}

public sealed class StockClient(IServerTransport transport) : IStockClient
{
    public async Task<IReadOnlyList<StockLocationSummary>> ListLocationsAsync(CancellationToken cancellationToken) =>
        await transport.SendAsync<List<StockLocationSummary>>(HttpMethod.Get, ApiRoutes.StockLocations, null, cancellationToken) ?? [];

    public async Task<StockAdjustmentResult> AdjustAsync(StockAdjustmentRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<StockAdjustmentResult>(HttpMethod.Post, ApiRoutes.StockAdjustments, request, cancellationToken))!;

    public async Task<StockTransferResult> TransferAsync(StockTransferRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<StockTransferResult>(HttpMethod.Post, ApiRoutes.StockTransfers, request, cancellationToken))!;
}
