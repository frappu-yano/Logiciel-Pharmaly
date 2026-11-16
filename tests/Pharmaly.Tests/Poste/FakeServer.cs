using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>
/// Faux serveur des tests du poste. Il est découpé comme les clients du poste : un fichier
/// par module (<c>FakeServer.Access.cs</c>, <c>FakeServer.Catalog.cs</c>,
/// <c>FakeServer.Admin.cs</c>…). Un nouveau module ajoute son fichier et une ligne dans
/// <see cref="Clients"/>.
/// </summary>
internal sealed partial class FakeServer
{
    public const string RegisterLogin = "caisse1";
    public const string RegisterPassword = "mot-de-passe";
    public const int EmployeeNumber = 12;
    public const string EmployeeSecret = "482615";
    public const string EmployeeName = "Samia Test";

    /// <summary>Identifiant de l'employé qui s'identifie avec <see cref="EmployeeNumber"/>.</summary>
    public static readonly Guid EmployeeId = Guid.NewGuid();

    public bool IsDown { get; set; }

    public bool HasPosteSession { get; set; }

    public bool EmployeeIdentified { get; set; }

    /// <summary>Droits effectifs renvoyés à l'identification.</summary>
    public IReadOnlyList<string> EmployeePermissions { get; set; } = [Permissions.ProductsView];

    public ServerClients Clients => new()
    {
        Access = this, Catalog = this, Admin = this, Stock = this, SupplierNotes = this,
        Sales = this, Customers = this, CashSession = this, Receivables = this, Returns = this,
    };

    /// <summary>L'employé de test possède tous les droits.</summary>
    public FakeServer AsAdministrator()
    {
        EmployeePermissions = Permissions.All.Select(permission => permission.Code).ToList();
        return this;
    }

    private void EnsureUp()
    {
        if (IsDown)
        {
            throw new HttpRequestException("serveur coupé");
        }
    }

    private void EnsureEmployee()
    {
        EnsureUp();
        if (!EmployeeIdentified)
        {
            throw new ServerRefusedException(ApiErrorCodes.EmployeeNotIdentified, "Identifiez-vous avec votre matricule et votre code.");
        }
    }

    private void EnsurePermission(string code)
    {
        EnsureEmployee();
        if (!EmployeePermissions.Contains(code))
        {
            throw new ServerRefusedException(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action.");
        }
    }
}
