using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « session de caisse » : la caisse du poste est-elle ouverte, son ouverture et sa fermeture.</summary>
public interface ICashSessionClient
{
    /// <summary>Session ouverte du compte de caisse du poste ; <c>null</c> si la caisse n'est pas ouverte.</summary>
    Task<CashSessionInfo?> GetCurrentSessionAsync(CancellationToken cancellationToken);

    /// <summary>Ouvre la caisse du poste avec le fond de caisse compté.</summary>
    Task<CashSessionInfo> OpenSessionAsync(OpenCashSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Totaux de la session ouverte du poste (droit « consulter les rapports de caisse »).</summary>
    Task<CashSessionSummary> GetSummaryAsync(CancellationToken cancellationToken);

    /// <summary>Entrée ou sortie d'espèces hors vente, motif obligatoire (droit « encaisser les paiements », D22).</summary>
    Task<CashMovementResult> RecordMovementAsync(CashMovementRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Ferme la caisse du poste avec les espèces comptées et renvoie le rapport de la session
    /// (attendu, compté, écart). Refusée tant qu'un paiement par carte attend sa réponse (D23).
    /// Rejouer la même demande (même <c>OperationId</c>) renvoie le rapport déjà enregistré.
    /// </summary>
    Task<CashSessionReport> CloseSessionAsync(CloseCashSessionRequest request, CancellationToken cancellationToken);
}

public sealed class CashSessionClient(IServerTransport transport) : ICashSessionClient
{
    public async Task<CashSessionInfo?> GetCurrentSessionAsync(CancellationToken cancellationToken) =>
        (await transport.SendAsync<CurrentCashSessionResponse>(HttpMethod.Get, CashRoutes.CurrentSession, null, cancellationToken))
        ?.Session;

    public async Task<CashSessionInfo> OpenSessionAsync(OpenCashSessionRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<CashSessionInfo>(HttpMethod.Post, CashRoutes.OpenSession, request, cancellationToken))!;

    public async Task<CashSessionSummary> GetSummaryAsync(CancellationToken cancellationToken) =>
        (await transport.SendAsync<CashSessionSummary>(HttpMethod.Get, CashRoutes.SessionSummary, null, cancellationToken))!;

    public async Task<CashMovementResult> RecordMovementAsync(CashMovementRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<CashMovementResult>(HttpMethod.Post, CashRoutes.Movements, request, cancellationToken))!;

    public async Task<CashSessionReport> CloseSessionAsync(CloseCashSessionRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<CashSessionReport>(HttpMethod.Post, ClosureRoutes.CloseSession, request, cancellationToken))!;
}
