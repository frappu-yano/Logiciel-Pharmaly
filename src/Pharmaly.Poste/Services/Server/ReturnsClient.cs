using System.Globalization;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Filtres de l'historique des retours du site ; les dates sont des jours entiers, bornes comprises.</summary>
/// <param name="From">Premier jour, ou <c>null</c> pour ne pas borner.</param>
/// <param name="To">Dernier jour, ou <c>null</c> pour ne pas borner.</param>
/// <param name="Ticket">Partie d'un numéro de ticket, d'un code-barres de ticket ou d'un numéro de retour, ou <c>null</c>.</param>
/// <param name="SaleId">Retours d'une vente précise, ou <c>null</c>.</param>
/// <param name="Limit">Nombre maximal de retours, ou <c>null</c> pour la limite du serveur.</param>
public sealed record ReturnsHistoryQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Ticket = null,
    Guid? SaleId = null,
    int? Limit = null);

/// <summary>Module « retours clients » : ticket à retourner, aperçu, enregistrement, historique et motifs.</summary>
public interface IReturnsClient
{
    /// <summary>Ce qui reste retournable sur un ticket (numéro ou code-barres) ; <c>null</c> s'il n'existe pas.</summary>
    Task<ReturnableSale?> FindReturnableAsync(string numberOrBarcode, CancellationToken cancellationToken);

    /// <summary>Remboursement calculé par le serveur, sans rien enregistrer.</summary>
    Task<ReturnPreview> PreviewAsync(PreviewReturnRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Enregistre un retour. Rejouer la même demande (même <c>OperationId</c>) renvoie le retour
    /// déjà enregistré sans rien refaire : c'est ainsi qu'on retente après une panne.
    /// </summary>
    Task<ReturnDetail> CreateReturnAsync(CreateReturnRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReturnSummary>> ListReturnsAsync(ReturnsHistoryQuery query, CancellationToken cancellationToken);

    /// <summary>Détail d'un retour du site ; <c>null</c> s'il n'existe pas.</summary>
    Task<ReturnDetail?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken);

    /// <summary>Motifs de retour : les actifs seulement, ou tous (administration).</summary>
    Task<IReadOnlyList<ReturnReason>> ListReasonsAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<ReturnReason> CreateReasonAsync(CreateReturnReasonRequest request, CancellationToken cancellationToken);

    /// <summary>Modifie le libellé, la destination ou l'état (désactivation) d'un motif.</summary>
    Task<ReturnReason> UpdateReasonAsync(Guid reasonId, UpdateReturnReasonRequest request, CancellationToken cancellationToken);
}

public sealed class ReturnsClient(IServerTransport transport) : IReturnsClient
{
    public Task<ReturnableSale?> FindReturnableAsync(string numberOrBarcode, CancellationToken cancellationToken) =>
        transport.SendAsync<ReturnableSale>(
            HttpMethod.Get, ReturnRoutes.ByTicket(numberOrBarcode.Trim()), null, cancellationToken, notFoundIsNull: true);

    public async Task<ReturnPreview> PreviewAsync(PreviewReturnRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ReturnPreview>(HttpMethod.Post, ReturnRoutes.Preview, request, cancellationToken))!;

    public async Task<ReturnDetail> CreateReturnAsync(CreateReturnRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ReturnDetail>(HttpMethod.Post, ReturnRoutes.Returns, request, cancellationToken))!;

    public async Task<IReadOnlyList<ReturnSummary>> ListReturnsAsync(ReturnsHistoryQuery query, CancellationToken cancellationToken) =>
        await transport.SendAsync<List<ReturnSummary>>(HttpMethod.Get, ReturnRoutes.Returns + ToQueryString(query), null, cancellationToken)
        ?? [];

    public Task<ReturnDetail?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken) =>
        transport.SendAsync<ReturnDetail>(HttpMethod.Get, ReturnRoutes.Return(returnId), null, cancellationToken, notFoundIsNull: true);

    public async Task<IReadOnlyList<ReturnReason>> ListReasonsAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await transport.SendAsync<List<ReturnReason>>(
            HttpMethod.Get, ReturnRoutes.Reasons + (includeInactive ? "?tous=true" : string.Empty), null, cancellationToken)
        ?? [];

    public async Task<ReturnReason> CreateReasonAsync(CreateReturnReasonRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ReturnReason>(HttpMethod.Post, ReturnRoutes.Reasons, request, cancellationToken))!;

    public async Task<ReturnReason> UpdateReasonAsync(
        Guid reasonId, UpdateReturnReasonRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ReturnReason>(HttpMethod.Put, ReturnRoutes.Reason(reasonId), request, cancellationToken))!;

    /// <summary>Paramètres attendus par le serveur : du, au, ticket, vente, limite.</summary>
    public static string ToQueryString(ReturnsHistoryQuery query)
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
        Add("ticket", query.Ticket);
        Add("vente", query.SaleId?.ToString());
        Add("limite", query.Limit?.ToString(CultureInfo.InvariantCulture));
        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
