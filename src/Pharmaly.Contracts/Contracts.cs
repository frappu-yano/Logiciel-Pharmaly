namespace Pharmaly.Contracts;

public sealed record HealthResponse(
    string Status,
    string Version,
    bool DatabaseReachable,
    int SchemaVersion,
    DateTimeOffset ServerTime);

public sealed record ProductSummary(
    Guid Id,
    long Code,
    string Name,
    decimal SalePrice,
    string? Barcode,
    // Stock vendable du site du poste (emplacements « VENDABLE » uniquement).
    decimal SellableStock = 0,
    // Faux : le produit se vend au poids ou au volume, les quantités décimales sont permises.
    bool SoldByUnit = true,
    // Faux : produit désactivé, renvoyé seulement quand la recherche le demande (« inactifs=true »).
    bool IsActive = true);

/// <summary>Marque ou catégorie existante.</summary>
public sealed record ReferenceName(Guid Id, string Name);

public static class ApiRoutes
{
    public const string Health = "/sante";
    /// <summary>GET « ?q=…&amp;limite=…&amp;inactifs=true » : « inactifs » inclut les produits désactivés.</summary>
    public const string ProductSearch = "/produits/recherche";

    /// <summary>GET « /produits/code-barres/{code}?inactifs=true ».</summary>
    public const string ProductByBarcode = "/produits/code-barres";

    /// <summary>Paramètre facultatif des deux routes ci-dessus : inclure les produits désactivés.</summary>
    public const string IncludeInactiveParameter = "inactifs";

    /// <summary>GET : marques existantes, par ordre alphabétique.</summary>
    public const string ProductBrands = "/produits/marques";

    /// <summary>GET : catégories existantes, par ordre alphabétique.</summary>
    public const string ProductCategories = "/produits/categories";
    public const string PosteSignIn = "/acces/poste";
    public const string EmployeeSignIn = "/acces/employe";
    public const string EmployeeSignOut = "/acces/employe/fin";
    public const string CurrentIdentity = "/acces/moi";
    public const string Employees = "/employes";
    public const string Stores = "/sites";

    /// <summary>POST : créer. GET et PUT « /produits/{id} » : fiche et modification.</summary>
    public const string Products = "/produits";

    /// <summary>PUT « /produits/{id}/prix-vente ».</summary>
    public const string ProductSalePriceSuffix = "prix-vente";
    public const string StockLocations = "/stock/emplacements";
    public const string StockAdjustments = "/stock/ajustements";
    public const string StockTransfers = "/stock/transferts";

    public static string Product(Guid id) => $"{Products}/{id}";

    public static string ProductSalePrice(Guid id) => $"{Products}/{id}/{ProductSalePriceSuffix}";
}

/// <summary>Réponse d'erreur du serveur : un code stable et un message lisible par l'employé.</summary>
public sealed record ApiError(string Code, string Message);

public static class ApiErrorCodes
{
    public const string PosteNotSignedIn = "POSTE_NON_CONNECTE";
    public const string EmployeeNotIdentified = "EMPLOYE_NON_IDENTIFIE";
    public const string PermissionDenied = "PERMISSION_REFUSEE";
    public const string InvalidCredentials = "IDENTIFIANTS_INVALIDES";
    public const string Locked = "COMPTE_VERROUILLE";
    public const string InvalidRequest = "DEMANDE_INVALIDE";
    public const string SelfModificationForbidden = "MODIFICATION_DE_SOI_INTERDITE";
    public const string NotFound = "INTROUVABLE";
    public const string Conflict = "CONFLIT";
    public const string InsufficientStock = "STOCK_INSUFFISANT";
    public const string FractionalQuantity = "QUANTITE_FRACTIONNAIRE_INTERDITE";

    /// <summary>L'identifiant d'opération a déjà servi pour une autre demande.</summary>
    public const string OperationMismatch = "OPERATION_DEJA_UTILISEE";
}

public static class ApiHeaders
{
    public const string PosteToken = "X-Pharmaly-Poste";
    public const string EmployeeToken = "X-Pharmaly-Employe";
}

public sealed record PosteSignInRequest(string Login, string Password);

public sealed record PosteSignInResponse(string Token, string RegisterName, string StoreName);

public sealed record EmployeeSignInRequest(int EmployeeNumber, string Secret);

/// <summary>StoreId : site du poste. SellableLocationId : son emplacement vendable par défaut (absent si le site n'en a pas).</summary>
public sealed record EmployeeSignInResponse(
    string Token,
    Guid EmployeeId,
    string EmployeeName,
    IReadOnlyList<string> Permissions,
    Guid StoreId = default,
    Guid? SellableLocationId = null);

/// <summary>StoreId : site du poste. SellableLocationId : son emplacement vendable par défaut (absent si le site n'en a pas).</summary>
public sealed record CurrentIdentityResponse(
    string RegisterName,
    string StoreName,
    Guid EmployeeId,
    string EmployeeName,
    IReadOnlyList<string> Permissions,
    Guid StoreId = default,
    Guid? SellableLocationId = null);

public sealed record PermissionGrant(string Code, Guid? StoreId);

public sealed record EmployeeSummary(Guid Id, int EmployeeNumber, string FullName, bool IsActive, IReadOnlyList<PermissionGrant> Permissions);

public sealed record CreateEmployeeRequest(int EmployeeNumber, string FullName, string Secret);

public sealed record SetPermissionsRequest(IReadOnlyList<PermissionGrant> Permissions);

public sealed record SetEmployeeStateRequest(bool IsActive);

public sealed record ResetSecretRequest(string Secret);

public sealed record StoreSummary(Guid Id, string Code, string Name, string Kind);

// ---------- Catalogue ----------
// Toute commande d'écriture porte un OperationId (UUID généré par le poste) : la rejouer
// renvoie le résultat déjà enregistré sans rien refaire.

/// <summary>Marque et catégorie sont créées à la volée si leur nom est nouveau.</summary>
public sealed record CreateProductRequest(
    Guid OperationId,
    string Name,
    IReadOnlyList<string> Barcodes,
    string? Brand,
    string? Category,
    decimal SalePrice,
    bool SoldByUnit);

/// <summary>Le prix de vente ne se modifie pas ici : voir <see cref="ChangeSalePriceRequest"/>.</summary>
public sealed record UpdateProductRequest(
    Guid OperationId,
    string Name,
    string? Brand,
    string? Category,
    bool IsActive,
    IReadOnlyList<string>? BarcodesToAdd,
    IReadOnlyList<string>? BarcodesToRemove);

public sealed record ChangeSalePriceRequest(Guid OperationId, decimal NewPrice);

public sealed record SalePriceChangeResult(Guid ProductId, decimal OldPrice, decimal NewPrice);

public sealed record ProductDetail(
    Guid Id,
    long Code,
    string Name,
    string? Brand,
    string? Category,
    decimal SalePrice,
    bool SoldByUnit,
    bool IsActive,
    IReadOnlyList<string> Barcodes,
    IReadOnlyList<ProductStockLine> Stock,
    IReadOnlyList<StockMovementLine> RecentMovements,
    IReadOnlyList<SalePriceHistoryLine> SalePriceHistory);

/// <summary>Solde d'un produit à un emplacement (0 si le produit n'y a jamais été).</summary>
public sealed record ProductStockLine(
    Guid LocationId,
    string LocationCode,
    string LocationName,
    string LocationKind,
    Guid StoreId,
    string StoreName,
    decimal Quantity);

public sealed record StockMovementLine(
    long Id,
    DateTimeOffset OccurredAt,
    Guid LocationId,
    string LocationName,
    string Type,
    decimal Quantity,
    decimal BalanceAfter,
    string EmployeeName,
    string? SourceType,
    string? SourceId,
    string? Note);

/// <summary>OldPrice est absent pour le prix fixé à la création.</summary>
public sealed record SalePriceHistoryLine(DateTimeOffset ChangedAt, decimal? OldPrice, decimal NewPrice, string EmployeeName);

// ---------- Stock ----------

public sealed record StockLocationSummary(Guid Id, string Code, string Name, string Kind, Guid StoreId, string StoreName);

/// <summary>
/// Ajustement d'inventaire. Renseigner soit NewQuantity (quantité comptée), soit
/// QuantityChange (correction en plus ou en moins), jamais les deux. Reason est obligatoire.
/// </summary>
public sealed record StockAdjustmentRequest(
    Guid OperationId,
    Guid ProductId,
    Guid LocationId,
    decimal? NewQuantity,
    decimal? QuantityChange,
    string Reason);

public sealed record StockAdjustmentResult(
    Guid ProductId,
    Guid LocationId,
    decimal QuantityBefore,
    decimal QuantityChange,
    decimal QuantityAfter);

public sealed record StockTransferLine(Guid ProductId, decimal Quantity);

/// <summary>Transfert simple : sortie et entrée dans la même transaction, tout ou rien.</summary>
public sealed record StockTransferRequest(
    Guid OperationId,
    Guid FromLocationId,
    Guid ToLocationId,
    IReadOnlyList<StockTransferLine> Lines,
    string? Note);

public sealed record StockTransferResultLine(Guid ProductId, decimal Quantity, decimal FromQuantityAfter, decimal ToQuantityAfter);

public sealed record StockTransferResult(Guid TransferId, IReadOnlyList<StockTransferResultLine> Lines);
