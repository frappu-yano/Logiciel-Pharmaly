using System.Globalization;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Filtres du tableau des impayés ; les dates sont des jours entiers, bornes comprises.</summary>
/// <param name="Status">Un état de <see cref="ReceivableStatuses"/>, ou <c>null</c> pour tous.</param>
/// <param name="CustomerId">Client, ou <c>null</c> pour tous.</param>
/// <param name="From">Premier jour, ou <c>null</c> pour ne pas borner.</param>
/// <param name="To">Dernier jour, ou <c>null</c> pour ne pas borner.</param>
/// <param name="Ticket">Partie d'un numéro ou d'un code-barres de ticket, ou <c>null</c>.</param>
/// <param name="Limit">Nombre maximal de lignes, ou <c>null</c> pour la limite du serveur.</param>
public sealed record ReceivablesQuery(
    string? Status = null,
    Guid? CustomerId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Ticket = null,
    int? Limit = null);

/// <summary>Module « impayés » : tableau, recherche par ticket, règlement et réponse du TPE.</summary>
public interface IReceivablesClient
{
    /// <summary>Tableau filtré et indicateurs (calculés sur toutes les créances, sans filtre).</summary>
    Task<ReceivablesTable> GetReceivablesAsync(ReceivablesQuery query, CancellationToken cancellationToken);

    /// <summary>Créance d'un ticket, par son numéro exact ou son code-barres ; <c>null</c> si aucune.</summary>
    Task<ReceivableLine?> FindByTicketAsync(string numberOrBarcode, CancellationToken cancellationToken);

    /// <summary>Un règlement, avec ses créances ; <c>null</c> s'il n'existe pas.</summary>
    Task<SettlementResult?> GetSettlementAsync(Guid settlementId, CancellationToken cancellationToken);

    /// <summary>
    /// Règle intégralement une ou plusieurs créances du même client. Rejouer la même demande
    /// (même <c>OperationId</c>) renvoie le règlement déjà enregistré sans rien refaire.
    /// </summary>
    Task<SettlementResult> SettleAsync(SettleReceivablesRequest request, CancellationToken cancellationToken);

    /// <summary>Réponse du TPE pour un règlement en attente : accepté (créances soldées) ou refusé (elles restent ouvertes).</summary>
    Task<SettlementResult> ResolveCardAsync(Guid settlementId, CardResultRequest request, CancellationToken cancellationToken);
}

public sealed class ReceivablesClient(IServerTransport transport) : IReceivablesClient
{
    public async Task<ReceivablesTable> GetReceivablesAsync(ReceivablesQuery query, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ReceivablesTable>(
            HttpMethod.Get, ReceivableRoutes.Receivables + ToQueryString(query), null, cancellationToken))
        ?? new ReceivablesTable(new ReceivableIndicators(0, 0, 0, 0, 0, 0), []);

    public Task<ReceivableLine?> FindByTicketAsync(string numberOrBarcode, CancellationToken cancellationToken) =>
        transport.SendAsync<ReceivableLine>(
            HttpMethod.Get, ReceivableRoutes.ByTicket(numberOrBarcode.Trim()), null, cancellationToken, notFoundIsNull: true);

    public Task<SettlementResult?> GetSettlementAsync(Guid settlementId, CancellationToken cancellationToken) =>
        transport.SendAsync<SettlementResult>(
            HttpMethod.Get, ReceivableRoutes.Settlement(settlementId), null, cancellationToken, notFoundIsNull: true);

    public async Task<SettlementResult> SettleAsync(SettleReceivablesRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SettlementResult>(HttpMethod.Post, ReceivableRoutes.Settlements, request, cancellationToken))!;

    public async Task<SettlementResult> ResolveCardAsync(Guid settlementId, CardResultRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SettlementResult>(
            HttpMethod.Post, ReceivableRoutes.SettlementCardResult(settlementId), request, cancellationToken))!;

    /// <summary>Paramètres attendus par le serveur : statut, client, du, au, q, limite.</summary>
    public static string ToQueryString(ReceivablesQuery query)
    {
        var parameters = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
            }
        }

        Add("statut", query.Status);
        Add("client", query.CustomerId?.ToString());
        Add("du", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("au", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("q", query.Ticket);
        Add("limite", query.Limit?.ToString(CultureInfo.InvariantCulture));
        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
