using Dapper;
using Npgsql;
using Pharmaly.Domain;

namespace Pharmaly.Server.Access;

/// <summary>
/// Mise en place d'un site depuis la ligne de commande du serveur, par la personne qui
/// l'installe. Les secrets générés ne sont affichés qu'une fois.
/// </summary>
public sealed class SiteSetup(NpgsqlDataSource dataSource)
{
    public const string MainLocationCode = "PRINCIPAL";
    public const string SetAsideLocationCode = "ECARTE";
    private const int EmployeeCodeDigits = 6;

    public async Task<Guid> InitializeSiteAsync(string code, string name, string kind)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var storeId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO stores (code, name, kind) VALUES (@code, @name, @kind) RETURNING id",
            new { code = code.ToUpperInvariant(), name, kind = kind.ToUpperInvariant() },
            transaction);
        // Chaque site a un stock vendable et un emplacement pour les produits mis à l'écart.
        await connection.ExecuteAsync(
            """
            INSERT INTO stock_locations (store_id, code, name, kind) VALUES
                (@storeId, @MainLocationCode, 'Stock principal', @sellable),
                (@storeId, @SetAsideLocationCode, 'Produits mis à l''écart', @setAside)
            """,
            new
            {
                storeId,
                MainLocationCode,
                SetAsideLocationCode,
                sellable = StockLocationKinds.Sellable,
                setAside = StockLocationKinds.SetAside,
            },
            transaction);
        await AuditLog.WriteAsync(
            connection, new AuditEntry(AuditActions.SiteInitialized, StoreId: storeId, Details: new { code, nom = name, type = kind }), transaction);
        await transaction.CommitAsync();
        return storeId;
    }

    /// <returns>Le mot de passe généré, à noter : il n'est stocké nulle part en clair.</returns>
    public async Task<string> CreateRegisterAccountAsync(string login, string displayName, string storeCode)
    {
        var password = Secrets.NewPassword();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var storeId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM stores WHERE code = @code", new { code = storeCode.ToUpperInvariant() }, transaction)
            ?? throw new InvalidOperationException($"Aucun site ne porte le code « {storeCode} ».");
        var id = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO register_accounts (login, display_name, store_id, password_hash)
            VALUES (@login, @displayName, @storeId, @hash) RETURNING id
            """,
            new { login = login.ToLowerInvariant(), displayName, storeId, hash = Secrets.Hash(password) },
            transaction);
        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(AuditActions.RegisterAccountCreated, RegisterAccountId: id, StoreId: storeId, Details: new { login }),
            transaction);
        await transaction.CommitAsync();
        return password;
    }

    /// <summary>Crée un employé possédant tous les droits sur tous les sites.</summary>
    /// <returns>Le code personnel généré.</returns>
    public async Task<string> CreateAdministratorAsync(int employeeNumber, string fullName)
    {
        var secret = Secrets.NewNumericCode(EmployeeCodeDigits);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO employees (employee_number, full_name, secret_hash) VALUES (@employeeNumber, @fullName, @hash) RETURNING id",
            new { employeeNumber, fullName, hash = Secrets.Hash(secret) },
            transaction);
        foreach (var permission in Permissions.All)
        {
            await connection.ExecuteAsync(
                "INSERT INTO employee_permissions (employee_id, permission_code) VALUES (@id, @Code)",
                new { id, permission.Code },
                transaction);
        }

        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.EmployeeCreated, EntityType: "employee", EntityId: id.ToString(),
                Details: new { matricule = employeeNumber, nom = fullName, origine = "installation", tousLesDroits = true }),
            transaction);
        await transaction.CommitAsync();
        return secret;
    }
}
