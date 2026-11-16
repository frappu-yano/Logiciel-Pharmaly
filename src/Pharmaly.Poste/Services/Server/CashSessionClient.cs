using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Module « session de caisse » : la caisse du poste est-elle ouverte, et son ouverture.</summary>
public interface ICashSessionClient
{
    /// <summary>Session ouverte du compte de caisse du poste ; <c>null</c> si la caisse n'est pas ouverte.</summary>
    Task<CashSessionInfo?> GetCurrentSessionAsync(CancellationToken cancellationToken);

    /// <summary>Ouvre la caisse du poste avec le fond de caisse compté.</summary>
    Task<CashSessionInfo> OpenSessionAsync(OpenCashSessionRequest request, CancellationToken cancellationToken);
}

public sealed class CashSessionClient(IServerTransport transport) : ICashSessionClient
{
    public async Task<CashSessionInfo?> GetCurrentSessionAsync(CancellationToken cancellationToken) =>
        (await transport.SendAsync<CurrentCashSessionResponse>(HttpMethod.Get, CashRoutes.CurrentSession, null, cancellationToken))
        ?.Session;

    public async Task<CashSessionInfo> OpenSessionAsync(OpenCashSessionRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<CashSessionInfo>(HttpMethod.Post, CashRoutes.OpenSession, request, cancellationToken))!;
}
