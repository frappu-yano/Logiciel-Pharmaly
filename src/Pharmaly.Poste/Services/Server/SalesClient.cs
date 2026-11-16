using System.Globalization;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Filtres de l'historique des ventes du site ; les dates sont des jours entiers, bornes comprises.</summary>
/// <param name="From">Premier jour, ou <c>null</c> pour ne pas borner.</param>
/// <param name="To">Dernier jour, ou <c>null</c> pour ne pas borner.</param>
/// <param name="Status">Un état de <see cref="SaleStatuses"/>, ou <c>null</c> pour tous.</param>
/// <param name="Ticket">Partie d'un numéro ou d'un code-barres de ticket, ou <c>null</c>.</param>
/// <param name="PaymentMode">Un mode de <see cref="SalePaymentModes"/>, ou <c>null</c> pour tous.</param>
/// <param name="Limit">Nombre maximal de tickets, ou <c>null</c> pour la limite du serveur.</param>
public sealed record SalesHistoryQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Status = null,
    string? Ticket = null,
    string? PaymentMode = null,
    int? Limit = null);

/// <summary>Module « ventes » : création, réponse du TPE, annulation, historique et tickets.</summary>
public interface ISalesClient
{
    /// <summary>
    /// Enregistre une vente. Rejouer la même demande (même <c>OperationId</c>) renvoie le
    /// ticket déjà enregistré sans rien refaire : c'est ainsi qu'on retente après une panne.
    /// </summary>
    Task<SaleDetail> CreateSaleAsync(CreateSaleRequest request, CancellationToken cancellationToken);

    /// <summary>Réponse du terminal de paiement pour une vente « en attente TPE » : acceptée ou refusée.</summary>
    Task<SaleDetail> ResolveCardAsync(Guid saleId, CardResultRequest request, CancellationToken cancellationToken);

    /// <summary>Annule une vente (motif obligatoire) : stock restitué, paiement remboursé ou créance annulée.</summary>
    Task<SaleDetail> CancelSaleAsync(Guid saleId, CancelSaleRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SaleSummary>> ListSalesAsync(SalesHistoryQuery query, CancellationToken cancellationToken);

    /// <summary>Détail d'un ticket du site ; <c>null</c> s'il n'existe pas.</summary>
    Task<SaleDetail?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken);

    /// <summary>Ticket retrouvé par son numéro exact ou par son code-barres imprimé ; <c>null</c> s'il n'existe pas.</summary>
    Task<SaleDetail?> FindSaleByTicketAsync(string numberOrBarcode, CancellationToken cancellationToken);

    /// <summary>
    /// Vrai si le produit se vend à l'unité (aucune fraction permise). La recherche de produits
    /// ne le dit pas : il faut lire la fiche du produit.
    /// </summary>
    Task<bool> IsSoldByUnitAsync(Guid productId, CancellationToken cancellationToken);
}

public sealed class SalesClient(IServerTransport transport) : ISalesClient
{
    public async Task<SaleDetail> CreateSaleAsync(CreateSaleRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SaleDetail>(HttpMethod.Post, SaleRoutes.Sales, request, cancellationToken))!;

    public async Task<SaleDetail> ResolveCardAsync(Guid saleId, CardResultRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SaleDetail>(HttpMethod.Post, SaleRoutes.CardResult(saleId), request, cancellationToken))!;

    public async Task<SaleDetail> CancelSaleAsync(Guid saleId, CancelSaleRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SaleDetail>(HttpMethod.Post, SaleRoutes.Cancellation(saleId), request, cancellationToken))!;

    public async Task<IReadOnlyList<SaleSummary>> ListSalesAsync(SalesHistoryQuery query, CancellationToken cancellationToken) =>
        await transport.SendAsync<List<SaleSummary>>(HttpMethod.Get, SaleRoutes.Sales + ToQueryString(query), null, cancellationToken) ?? [];

    public Task<SaleDetail?> GetSaleAsync(Guid saleId, CancellationToken cancellationToken) =>
        transport.SendAsync<SaleDetail>(HttpMethod.Get, SaleRoutes.Sale(saleId), null, cancellationToken, notFoundIsNull: true);

    public Task<SaleDetail?> FindSaleByTicketAsync(string numberOrBarcode, CancellationToken cancellationToken) =>
        transport.SendAsync<SaleDetail>(HttpMethod.Get, SaleRoutes.ByTicket(numberOrBarcode), null, cancellationToken, notFoundIsNull: true);

    public async Task<bool> IsSoldByUnitAsync(Guid productId, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ProductDetail>(HttpMethod.Get, ApiRoutes.Product(productId), null, cancellationToken))!.SoldByUnit;

    /// <summary>Paramètres attendus par le serveur : du, au, statut, mode, numero, limite.</summary>
    public static string ToQueryString(SalesHistoryQuery query)
    {
        var parameters = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
            }
        }

        Add("du", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("au", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("statut", query.Status);
        Add("mode", query.PaymentMode);
        Add("numero", query.Ticket);
        Add("limite", query.Limit?.ToString(CultureInfo.InvariantCulture));
        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
