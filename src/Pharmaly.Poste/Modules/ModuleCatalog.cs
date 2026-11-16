using Avalonia.Controls;
using Pharmaly.Domain;
using Pharmaly.Poste.Modules.Administration;
using Pharmaly.Poste.Modules.Cash;
using Pharmaly.Poste.Modules.Diagnostic;
using Pharmaly.Poste.Modules.Products;
using Pharmaly.Poste.Modules.Sales;
using Pharmaly.Poste.Modules.SupplierNotes;
using Pharmaly.Poste.Services.Server;
using Pharmaly.Poste.ViewModels;

namespace Pharmaly.Poste.Modules;

/// <summary>Ce dont un module a besoin pour être construit.</summary>
/// <param name="Clients">Clients du serveur, un par module.</param>
/// <param name="Session">Employé identifié et ses droits (confort d'affichage : le serveur contrôle tout).</param>
/// <param name="Status">État de la liaison avec le serveur.</param>
/// <param name="SearchDelay">Attente après la dernière frappe avant de lancer une recherche.</param>
public sealed record ModuleContext(ServerClients Clients, SessionViewModel Session, ServerStatusViewModel Status, TimeSpan SearchDelay)
{
    /// <summary>Temps écoulé entre le lancement du programme et l'affichage de la fenêtre.</summary>
    public double StartupSeconds { get; set; }
}

/// <summary>Description d'un module de la fenêtre principale.</summary>
/// <param name="Key">Identifiant stable du module.</param>
/// <param name="Title">Nom affiché dans la barre de navigation.</param>
/// <param name="Permissions">Le module n'apparaît que si l'employé possède au moins un de ces droits.</param>
/// <param name="CreateViewModel">Construit le modèle de vue, à la première ouverture du module.</param>
/// <param name="CreateView">Construit la vue (un <see cref="Control"/> qui implémente <see cref="IModuleView"/>).</param>
public sealed record ModuleDefinition(
    string Key,
    string Title,
    IReadOnlyList<string> Permissions,
    Func<ModuleContext, ModuleViewModel> CreateViewModel,
    Func<Control> CreateView);

public static class ModuleKeys
{
    public const string Sales = "vente";
    public const string SupplierNotes = "bons";
    public const string Cash = "caisse";
    public const string Products = "produits";
    public const string Administration = "administration";
    public const string Diagnostic = "diagnostic";
}

/// <summary>
/// Liste des modules, dans l'ordre de la barre de navigation. La position fixe le raccourci :
/// le premier module s'ouvre avec F1 (ou Ctrl+1), le deuxième avec F2, etc.
/// </summary>
public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new(ModuleKeys.Sales, "Vente",
            [
                Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.DiscountsApply,
                Permissions.ReturnsCreate, Permissions.SalesCancel, Permissions.SalesHistoryView,
            ],
            context => new SalesViewModel(
                context.Clients.Sales, context.Clients.Customers, context.Clients.CashSession, context.Clients.Catalog,
                SalesRights.From(context.Session), context.SearchDelay, new NoTicketPrinter()),
            () => new SalesView()),

        new(ModuleKeys.SupplierNotes, "Bons fournisseurs",
            [
                Permissions.NotesView, Permissions.NotesEnter, Permissions.NotesEdit,
                Permissions.NotesValidate, Permissions.NotesCancel,
            ],
            context => new SupplierNotesViewModel(
                context.Clients.SupplierNotes, context.Clients.Catalog, context.Session.HasPermission,
                new NoteHeartbeat(NoteHeartbeat.DefaultInterval), context.SearchDelay),
            () => new SupplierNotesView()),

        new(ModuleKeys.Cash, "Caisse",
            [
                Permissions.RegisterOpen, Permissions.RegisterClose, Permissions.RegisterReportsView,
                Permissions.ReceivablesView, Permissions.ReceivablesSettle,

                // Entrées et sorties d'espèces (D22).
                Permissions.PaymentsCollect,
            ],
            context => new CashViewModel(
                context.Clients.Receivables, context.Clients.Customers, context.Clients.CashSession, context.Clients.Closures,
                CashRights.From(context.Session)),
            () => new CashView()),

        new(ModuleKeys.Products, "Produits",
            [
                Permissions.ProductsView, Permissions.ProductsCreate, Permissions.ProductsEdit, Permissions.SalePriceEdit,
                Permissions.StockAdjust, Permissions.StockTransfer,
            ],
            context => new ProductsViewModel(
                context.Clients.Catalog, context.Clients.Stock, context.Session.HasPermission, context.SearchDelay),
            () => new ProductsView()),

        new(ModuleKeys.Administration, "Administration",
            [Permissions.UsersAdminister],
            context => new AdministrationViewModel(context.Clients.Admin, context.Session.EmployeeId),
            () => new AdministrationView()),

        // L'écran d'essai cherche dans le catalogue : il demande le droit de consulter les produits.
        new(ModuleKeys.Diagnostic, "Diagnostic",
            [Permissions.ProductsView],
            context => new DiagnosticViewModel(context.Clients.Catalog, context.Status, context.SearchDelay)
            {
                StartupSeconds = context.StartupSeconds,
            },
            () => new DiagnosticView()),
    ];
}
