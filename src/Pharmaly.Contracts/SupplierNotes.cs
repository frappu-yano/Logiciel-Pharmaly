namespace Pharmaly.Contracts;

// ---------- Bons fournisseurs ----------
// Toute commande d'écriture porte un OperationId (UUID généré par le poste). Les routes
// du verrou (prendre, renouveler, libérer) n'en portent pas : les rejouer est sans effet.

public static class SupplierNoteRoutes
{
    /// <summary>GET : liste (« ?q= »). POST : créer.</summary>
    public const string Suppliers = "/fournisseurs";

    /// <summary>GET : liste paginée. POST : créer un bon.</summary>
    public const string Notes = "/bons";

    /// <summary>GET « /bons/produits/code-barres/{code} » : produit scanné, avec son dernier prix d'achat.</summary>
    public const string ProductByBarcode = "/bons/produits/code-barres";

    /// <summary>GET « /bons/produits/{id} » : même réponse, pour un produit choisi par son nom.</summary>
    public const string Products = "/bons/produits";

    public const string LockSuffix = "verrou";
    public const string ForcedReleaseSuffix = "verrou/liberation-forcee";
    public const string LinesSuffix = "lignes";
    public const string CancellationSuffix = "annulation";
    public const string ValidationSuffix = "validation";

    /// <summary>Paramètres de la liste des bons.</summary>
    public const string StatusParameter = "etat";
    public const string SupplierParameter = "fournisseur";
    public const string FromParameter = "du";
    public const string ToParameter = "au";
    public const string PageParameter = "page";
    public const string PageSizeParameter = "taille";

    public static string Note(Guid noteId) => $"{Notes}/{noteId}";

    /// <summary>POST : prendre ou reprendre. PUT : renouveler (battement). DELETE : libérer.</summary>
    public static string Lock(Guid noteId) => $"{Notes}/{noteId}/{LockSuffix}";

    public static string ForcedRelease(Guid noteId) => $"{Notes}/{noteId}/{ForcedReleaseSuffix}";

    /// <summary>POST : ajouter une ligne.</summary>
    public static string Lines(Guid noteId) => $"{Notes}/{noteId}/{LinesSuffix}";

    /// <summary>PUT : modifier la ligne.</summary>
    public static string Line(Guid noteId, Guid lineId) => $"{Notes}/{noteId}/{LinesSuffix}/{lineId}";

    public static string LineCancellation(Guid noteId, Guid lineId) =>
        $"{Notes}/{noteId}/{LinesSuffix}/{lineId}/{CancellationSuffix}";

    public static string Validation(Guid noteId) => $"{Notes}/{noteId}/{ValidationSuffix}";

    public static string Cancellation(Guid noteId) => $"{Notes}/{noteId}/{CancellationSuffix}";

    public static string ProductInfo(Guid productId) => $"{Products}/{productId}";

    public static string ProductInfoByBarcode(string barcode) => $"{ProductByBarcode}/{Uri.EscapeDataString(barcode)}";
}

/// <summary>Codes d'erreur propres aux bons ; les codes généraux sont dans <see cref="ApiErrorCodes"/>.</summary>
public static class SupplierNoteErrorCodes
{
    /// <summary>Le bon est tenu par un autre employé (le message le nomme).</summary>
    public const string LockedByOther = "BON_VERROUILLE";

    /// <summary>L'employé ne tient pas le bon : il doit le prendre avant d'écrire.</summary>
    public const string LockNotHeld = "BON_NON_RESERVE";

    /// <summary>La ligne a changé depuis sa lecture : recharger le bon.</summary>
    public const string StaleVersion = "VERSION_PERIMEE";

    /// <summary>Réduction ou annulation impossible : la marchandise a déjà quitté l'emplacement.</summary>
    public const string StockAlreadyGone = "STOCK_DEJA_SORTI";

    public const string NoteCancelled = "BON_ANNULE";
    public const string LineCancelled = "LIGNE_ANNULEE";

    /// <summary>Aucun produit ne porte ce code-barres : l'écran peut proposer la création rapide.</summary>
    public const string UnknownBarcode = "PRODUIT_INCONNU";

    /// <summary>Un bon sans ligne ne se valide pas.</summary>
    public const string EmptyNote = "BON_VIDE";
}

public static class SupplierNoteStatuses
{
    public const string InProgress = "EN_SAISIE";
    public const string Validated = "VALIDE";
    public const string Cancelled = "ANNULE";

    public static bool IsKnown(string status) => status is InProgress or Validated or Cancelled;
}

/// <summary>Événements de l'historique d'un bon.</summary>
public static class SupplierNoteEvents
{
    public const string Created = "CREATION";
    public const string LineAdded = "LIGNE_AJOUTEE";
    public const string LineUpdated = "LIGNE_MODIFIEE";
    public const string LineCancelled = "LIGNE_ANNULEE";
    public const string Validated = "VALIDATION";
    public const string Cancelled = "ANNULATION";
    public const string LockForced = "VERROU_FORCE";
}

public sealed record SupplierSummary(Guid Id, string Name);

public sealed record CreateSupplierRequest(Guid OperationId, string Name);

/// <summary>Le bon créé est aussitôt réservé à l'employé qui le crée.</summary>
public sealed record CreateSupplierNoteRequest(
    Guid OperationId,
    Guid SupplierId,
    Guid LocationId,
    string SupplierReference,
    DateOnly NoteDate);

/// <summary>
/// Qui tient le bon. IsExpired : le bail est échu, un autre employé peut prendre le bon.
/// </summary>
public sealed record SupplierNoteLock(
    Guid EmployeeId,
    string EmployeeName,
    string RegisterName,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    bool HeldByMe,
    bool IsExpired);

/// <summary>LineCount et TotalAmount ne comptent pas les lignes annulées.</summary>
public sealed record SupplierNoteSummary(
    Guid Id,
    long Number,
    Guid SupplierId,
    string SupplierName,
    Guid LocationId,
    string LocationName,
    Guid StoreId,
    string StoreName,
    string SupplierReference,
    DateOnly NoteDate,
    string Status,
    int LineCount,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    string CreatedByName,
    SupplierNoteLock? Lock);

public sealed record SupplierNotePage(IReadOnlyList<SupplierNoteSummary> Items, int Total, int Page, int PageSize);

/// <summary>
/// Effet du prix d'achat d'une ligne sur la marge, le prix de vente restant inchangé.
/// Marge = prix de vente − prix d'achat ; pourcentage = marge / prix de vente × 100,
/// arrondi à deux décimales (absent si le prix de vente est nul). « Avant » est absent
/// quand le produit n'avait aucun prix d'achat connu.
/// </summary>
public sealed record MarginVariation(
    decimal SalePrice,
    decimal? PreviousPurchasePrice,
    decimal NewPurchasePrice,
    decimal? MarginBefore,
    decimal? MarginBeforePercent,
    decimal MarginAfter,
    decimal? MarginAfterPercent);

public sealed record SupplierNoteLine(
    Guid Id,
    int Position,
    Guid ProductId,
    long ProductCode,
    string ProductName,
    string? Barcode,
    decimal Quantity,
    decimal PurchasePrice,
    decimal LineTotal,
    int Version,
    bool IsCancelled,
    MarginVariation Margin);

public sealed record SupplierNoteHistoryEntry(
    DateTimeOffset OccurredAt,
    string Event,
    string? StatusBefore,
    string StatusAfter,
    string EmployeeName,
    Guid? LineId,
    string Description);

public sealed record SupplierNoteDetail(
    SupplierNoteSummary Note,
    IReadOnlyList<SupplierNoteLine> Lines,
    IReadOnlyList<SupplierNoteHistoryEntry> History);

public sealed record AddSupplierNoteLineRequest(Guid OperationId, Guid ProductId, decimal Quantity, decimal PurchasePrice);

/// <summary>ExpectedVersion : la version de la ligne telle que l'écran l'a lue.</summary>
public sealed record UpdateSupplierNoteLineRequest(Guid OperationId, int ExpectedVersion, decimal Quantity, decimal PurchasePrice);

public sealed record CancelSupplierNoteLineRequest(Guid OperationId, int ExpectedVersion);

/// <summary>
/// Résultat d'une écriture sur une ligne. StockChange est la quantité réellement entrée
/// (ou sortie, si négative) du stock de l'emplacement du bon par cette commande.
/// </summary>
public sealed record SupplierNoteLineResult(
    SupplierNoteLine Line,
    decimal StockBefore,
    decimal StockChange,
    decimal StockAfter,
    int NoteLineCount,
    decimal NoteTotalAmount);

public sealed record ValidateSupplierNoteRequest(Guid OperationId);

public sealed record CancelSupplierNoteRequest(Guid OperationId, string? Reason);

/// <summary>
/// Produit proposé à la saisie d'une ligne, avec son dernier prix d'achat connu (absent
/// s'il n'a jamais été acheté).
/// </summary>
public sealed record NoteProductInfo(
    Guid Id,
    long Code,
    string Name,
    string? Barcode,
    decimal SalePrice,
    bool SoldByUnit,
    bool IsActive,
    decimal? LastPurchasePrice,
    string? LastSupplierName,
    DateOnly? LastPurchaseDate);
