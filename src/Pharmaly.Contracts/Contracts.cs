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
    string? Barcode);

public static class ApiRoutes
{
    public const string Health = "/sante";
    public const string ProductSearch = "/produits/recherche";
    public const string ProductByBarcode = "/produits/code-barres";
    public const string PosteSignIn = "/acces/poste";
    public const string EmployeeSignIn = "/acces/employe";
    public const string EmployeeSignOut = "/acces/employe/fin";
    public const string CurrentIdentity = "/acces/moi";
    public const string Employees = "/employes";
    public const string Stores = "/sites";
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
}

public static class ApiHeaders
{
    public const string PosteToken = "X-Pharmaly-Poste";
    public const string EmployeeToken = "X-Pharmaly-Employe";
}

public sealed record PosteSignInRequest(string Login, string Password);

public sealed record PosteSignInResponse(string Token, string RegisterName, string StoreName);

public sealed record EmployeeSignInRequest(int EmployeeNumber, string Secret);

public sealed record EmployeeSignInResponse(string Token, Guid EmployeeId, string EmployeeName, IReadOnlyList<string> Permissions);

public sealed record CurrentIdentityResponse(string RegisterName, string StoreName, Guid EmployeeId, string EmployeeName, IReadOnlyList<string> Permissions);

public sealed record PermissionGrant(string Code, Guid? StoreId);

public sealed record EmployeeSummary(Guid Id, int EmployeeNumber, string FullName, bool IsActive, IReadOnlyList<PermissionGrant> Permissions);

public sealed record CreateEmployeeRequest(int EmployeeNumber, string FullName, string Secret);

public sealed record SetPermissionsRequest(IReadOnlyList<PermissionGrant> Permissions);

public sealed record SetEmployeeStateRequest(bool IsActive);

public sealed record ResetSecretRequest(string Secret);

public sealed record StoreSummary(Guid Id, string Code, string Name, string Kind);
