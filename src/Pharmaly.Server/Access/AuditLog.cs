using System.Data;
using System.Text.Json;
using Dapper;

namespace Pharmaly.Server.Access;

public static class AuditActions
{
    public const string PosteSignedIn = "POSTE_CONNEXION";
    public const string PosteSignInRefused = "POSTE_CONNEXION_REFUSEE";
    public const string EmployeeIdentified = "EMPLOYE_IDENTIFICATION";
    public const string EmployeeIdentificationRefused = "EMPLOYE_IDENTIFICATION_REFUSEE";
    public const string AccountLocked = "COMPTE_VERROUILLE";
    public const string EmployeeSignedOut = "EMPLOYE_FIN_DE_SESSION";
    public const string PermissionDenied = "PERMISSION_REFUSEE";
    public const string EmployeeCreated = "EMPLOYE_CREE";
    public const string PermissionsChanged = "PERMISSIONS_MODIFIEES";
    public const string EmployeeStateChanged = "EMPLOYE_ETAT_MODIFIE";
    public const string SecretReset = "CODE_REINITIALISE";
    public const string SiteInitialized = "SITE_INITIALISE";
    public const string RegisterAccountCreated = "COMPTE_CAISSE_CREE";
    public const string ProductCreated = "PRODUIT_CREE";
    public const string ProductUpdated = "PRODUIT_MODIFIE";
    public const string SalePriceChanged = "PRIX_VENTE_MODIFIE";
    public const string StockAdjusted = "STOCK_AJUSTE";
    public const string StockTransferred = "STOCK_TRANSFERE";
}

public sealed record AuditEntry(
    string Action,
    Guid? EmployeeId = null,
    Guid? RegisterAccountId = null,
    Guid? StoreId = null,
    string? EntityType = null,
    string? EntityId = null,
    object? Details = null);

public static class AuditLog
{
    /// <summary>Écrit dans le journal avec la connexion (et la transaction) de l'opération auditée.</summary>
    public static Task WriteAsync(IDbConnection connection, AuditEntry entry, IDbTransaction? transaction = null) =>
        connection.ExecuteAsync(
            """
            INSERT INTO audit_logs (action, employee_id, register_account_id, store_id, entity_type, entity_id, details)
            VALUES (@Action, @EmployeeId, @RegisterAccountId, @StoreId, @EntityType, @EntityId, @details::jsonb)
            """,
            new
            {
                entry.Action,
                entry.EmployeeId,
                entry.RegisterAccountId,
                entry.StoreId,
                entry.EntityType,
                entry.EntityId,
                details = JsonSerializer.Serialize(entry.Details ?? new { }),
            },
            transaction);
}
