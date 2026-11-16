using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Session de caisse du poste : fermée au départ, comme un poste en début de journée.</summary>
internal sealed partial class FakeServer : ICashSessionClient
{
    /// <summary>Session ouverte du poste ; <c>null</c> quand la caisse n'est pas ouverte.</summary>
    public CashSessionInfo? CashSession { get; set; }

    public List<OpenCashSessionRequest> OpenSessionRequests { get; } = [];

    /// <summary>Pannes de réseau (demande perdue) pour les prochaines ouvertures de caisse.</summary>
    public int FailNextSessionOpenings { get; set; }

    /// <summary>La caisse est déjà ouverte, avec ce fond de caisse.</summary>
    public FakeServer WithOpenCashSession(decimal openingFloat = 5000m)
    {
        CashSession = new CashSessionInfo(
            Guid.NewGuid(), Guid.NewGuid(), "Caisse 1", Didouche.Id, "OUVERTE", openingFloat, EmployeeId, EmployeeName, DateTimeOffset.Now);
        return this;
    }

    public Task<CashSessionInfo?> GetCurrentSessionAsync(CancellationToken cancellationToken)
    {
        EnsureEmployee();
        return Task.FromResult(CashSession);
    }

    public Task<CashSessionInfo> OpenSessionAsync(OpenCashSessionRequest request, CancellationToken cancellationToken)
    {
        OpenSessionRequests.Add(request);
        if (FailNextSessionOpenings > 0)
        {
            FailNextSessionOpenings--;
            throw new HttpRequestException("réseau coupé");
        }

        EnsurePermission(Permissions.RegisterOpen);
        if (CashSession is not null)
        {
            throw new ServerRefusedException(CashErrorCodes.SessionAlreadyOpen, "La caisse de ce poste est déjà ouverte.");
        }

        WithOpenCashSession(request.OpeningFloat);
        return Task.FromResult(CashSession!);
    }
}
