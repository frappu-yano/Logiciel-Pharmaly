using Pharmaly.Contracts;
using Pharmaly.Poste.Services;

namespace Pharmaly.Tests.Poste;

internal sealed class FakeServer : IServerClient
{
    public static readonly ProductSummary Creme = new(Guid.NewGuid(), 100001, "Crème hydratante 50 ml", 1850.10m, "2000000000015");
    public static readonly ProductSummary Serum = new(Guid.NewGuid(), 100002, "Sérum nuit 30 ml", 4200.20m, "2000000000022");

    private readonly ProductSummary[] catalogue = [Creme, Serum];

    public const string RegisterLogin = "caisse1";
    public const string RegisterPassword = "mot-de-passe";
    public const int EmployeeNumber = 12;
    public const string EmployeeSecret = "482615";

    public bool IsDown { get; set; }

    public bool HasPosteSession { get; set; }

    public bool EmployeeIdentified { get; set; }

    /// <summary>Simule un employé sans le droit de consulter les produits.</summary>
    public bool DenyProducts { get; set; }

    private void EnsureUp()
    {
        if (IsDown)
        {
            throw new HttpRequestException("serveur coupé");
        }
    }

    private void EnsureAllowed()
    {
        EnsureUp();
        if (!EmployeeIdentified)
        {
            throw new ServerRefusedException(ApiErrorCodes.EmployeeNotIdentified, "Identifiez-vous avec votre matricule et votre code.");
        }

        if (DenyProducts)
        {
            throw new ServerRefusedException(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action.");
        }
    }

    public Task<PosteSignInResponse> SignInPosteAsync(string login, string password, CancellationToken cancellationToken)
    {
        EnsureUp();
        if (login != RegisterLogin || password != RegisterPassword)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidCredentials, "Identifiant ou code incorrect.");
        }

        HasPosteSession = true;
        return Task.FromResult(new PosteSignInResponse("jeton-poste", "Caisse 1", "Pharmaly 1 — Didouche"));
    }

    public Task<EmployeeSignInResponse> SignInEmployeeAsync(int employeeNumber, string secret, CancellationToken cancellationToken)
    {
        EnsureUp();
        if (!HasPosteSession)
        {
            throw new ServerRefusedException(ApiErrorCodes.PosteNotSignedIn, "Ce poste n'est pas connecté avec un compte de caisse.");
        }

        if (employeeNumber != EmployeeNumber || secret != EmployeeSecret)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidCredentials, "Identifiant ou code incorrect.");
        }

        EmployeeIdentified = true;
        return Task.FromResult(new EmployeeSignInResponse("jeton-employe", Guid.NewGuid(), "Samia Test", ["produits.consulter"]));
    }

    public Task SignOutEmployeeAsync(CancellationToken cancellationToken)
    {
        EmployeeIdentified = false;
        return Task.CompletedTask;
    }

    public void ForgetPosteSession()
    {
        HasPosteSession = false;
        EmployeeIdentified = false;
    }

    public Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken) =>
        IsDown
            ? throw new HttpRequestException("serveur coupé")
            : Task.FromResult<HealthResponse?>(new HealthResponse("OK", "0.1.0", true, 1, DateTimeOffset.Now));

    public Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken)
    {
        EnsureAllowed();
        return Task.FromResult<IReadOnlyList<ProductSummary>>(
            catalogue.Where(product => product.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        EnsureAllowed();
        return Task.FromResult(catalogue.FirstOrDefault(product => product.Barcode == barcode));
    }
}
