namespace Pharmaly.Domain;

public sealed record PermissionDefinition(string Code, string Label, string Group);

/// <summary>
/// Droits attribuables individuellement à chaque employé. La liste vient du cahier des
/// charges ; un rôle n'est qu'un modèle de départ qui coche plusieurs de ces droits.
/// </summary>
public static class Permissions
{
    public const string ProductsView = "produits.consulter";
    public const string ProductsCreate = "produits.creer";
    public const string ProductsEdit = "produits.modifier";
    public const string SalePriceEdit = "produits.modifier_prix_vente";
    public const string NotesView = "bons.consulter";
    public const string NotesEnter = "bons.saisir";
    public const string NotesEdit = "bons.modifier";
    public const string NotesValidate = "bons.valider";
    public const string NotesCancel = "bons.annuler";
    public const string SalesCreate = "ventes.vendre";
    public const string PaymentsCollect = "ventes.encaisser";
    public const string DiscountsApply = "ventes.remise";
    public const string ReturnsCreate = "ventes.retour";
    public const string SalesCancel = "ventes.annuler";
    public const string SalesHistoryView = "ventes.historique";
    public const string RegisterOpen = "caisse.ouvrir";
    public const string RegisterClose = "caisse.cloturer";
    public const string RegisterReportsView = "caisse.rapports";
    public const string ReceivablesView = "impayes.consulter";
    public const string ReceivablesSettle = "impayes.regler";
    public const string UsersAdminister = "admin.utilisateurs";

    // Ajouts proposés avec le moteur de stock : absents de la liste du cahier des charges, à valider.
    public const string StockAdjust = "stock.ajuster";
    public const string StockTransfer = "stock.transferer";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(ProductsView, "Consulter les produits", "Produits"),
        new(ProductsCreate, "Créer un produit", "Produits"),
        new(ProductsEdit, "Modifier un produit", "Produits"),
        new(SalePriceEdit, "Modifier un prix de vente", "Produits"),
        new(NotesView, "Consulter les bons", "Bons fournisseurs"),
        new(NotesEnter, "Créer et saisir un bon", "Bons fournisseurs"),
        new(NotesEdit, "Modifier un bon", "Bons fournisseurs"),
        new(NotesValidate, "Valider un bon", "Bons fournisseurs"),
        new(NotesCancel, "Annuler un bon", "Bons fournisseurs"),
        new(SalesCreate, "Effectuer une vente", "Ventes"),
        new(PaymentsCollect, "Encaisser les paiements", "Ventes"),
        new(DiscountsApply, "Appliquer une remise", "Ventes"),
        new(ReturnsCreate, "Effectuer un retour", "Ventes"),
        new(SalesCancel, "Annuler une vente", "Ventes"),
        new(SalesHistoryView, "Consulter l'historique des ventes", "Ventes"),
        new(RegisterOpen, "Ouvrir une caisse", "Caisse"),
        new(RegisterClose, "Clôturer une caisse", "Caisse"),
        new(RegisterReportsView, "Consulter les rapports de caisse", "Caisse"),
        new(ReceivablesView, "Consulter les impayés", "Caisse"),
        new(ReceivablesSettle, "Régler les impayés", "Caisse"),
        new(UsersAdminister, "Administrer les utilisateurs et permissions", "Administration"),
        new(StockAdjust, "Ajuster un stock (inventaire)", "Stock"),
        new(StockTransfer, "Transférer du stock entre emplacements", "Stock"),
    ];

    private static readonly HashSet<string> KnownCodes = All.Select(permission => permission.Code).ToHashSet();

    public static bool IsKnown(string code) => KnownCodes.Contains(code);
}
