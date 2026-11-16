namespace Pharmaly.Poste.Services.Server;

/// <summary>
/// Les clients par module, tous construits sur le même transport. Pour un nouveau module
/// (bons, ventes, caisse…) : créer <c>XxxClient.cs</c> avec son interface dans ce dossier,
/// puis ajouter ici une propriété et une ligne dans <see cref="Create"/>.
/// </summary>
public sealed class ServerClients
{
    public required IAccessClient Access { get; init; }

    public required ICatalogClient Catalog { get; init; }

    public required IAdminClient Admin { get; init; }

    public required IStockClient Stock { get; init; }

    public required ISupplierNotesClient SupplierNotes { get; init; }

    public required ISalesClient Sales { get; init; }

    public required ICustomersClient Customers { get; init; }

    public required ICashSessionClient CashSession { get; init; }

    public required IReceivablesClient Receivables { get; init; }

    public required IReturnsClient Returns { get; init; }

    public static ServerClients Create(IServerTransport transport) => new()
    {
        Access = new AccessClient(transport),
        Catalog = new CatalogClient(transport),
        Admin = new AdminClient(transport),
        Stock = new StockClient(transport),
        SupplierNotes = new SupplierNotesClient(transport),
        Sales = new SalesClient(transport),
        Customers = new CustomersClient(transport),
        CashSession = new CashSessionClient(transport),
        Receivables = new ReceivablesClient(transport),
        Returns = new ReturnsClient(transport),
    };
}
