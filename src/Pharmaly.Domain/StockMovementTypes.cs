namespace Pharmaly.Domain;

/// <summary>Types de mouvement de stock, tels qu'ils sont inscrits dans le journal.</summary>
public static class StockMovementTypes
{
    public const string NoteEntry = "ENTREE_BON";
    public const string NoteCorrection = "CORRECTION_BON";
    public const string NoteCancellation = "ANNULATION_BON";
    public const string Sale = "VENTE";
    public const string SaleCancellation = "ANNULATION_VENTE";
    public const string Return = "RETOUR";
    public const string TransferOut = "TRANSFERT_SORTIE";
    public const string TransferIn = "TRANSFERT_ENTREE";
    public const string Adjustment = "AJUSTEMENT";

    private static readonly HashSet<string> Known =
    [
        NoteEntry, NoteCorrection, NoteCancellation, Sale, SaleCancellation, Return, TransferOut, TransferIn, Adjustment,
    ];

    public static bool IsKnown(string type) => Known.Contains(type);
}

/// <summary>Nature d'un emplacement de stock.</summary>
public static class StockLocationKinds
{
    /// <summary>Stock que l'on peut vendre.</summary>
    public const string Sellable = "VENDABLE";

    /// <summary>Produits mis à l'écart (défectueux…) : jamais proposés à la vente.</summary>
    public const string SetAside = "ECARTE";
}
