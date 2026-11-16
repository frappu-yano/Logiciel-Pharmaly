using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Stock;

namespace Pharmaly.Server.Access;

public sealed record AccessFailure(int StatusCode, ApiError Error);

public sealed class AccessService(NpgsqlDataSource dataSource)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PosteSessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan EmployeeSessionLifetime = TimeSpan.FromHours(12);

    private static readonly AccessFailure InvalidCredentials = new(
        StatusCodes.Status401Unauthorized,
        new ApiError(ApiErrorCodes.InvalidCredentials, "Identifiant ou code incorrect."));

    private static readonly AccessFailure Locked = new(
        StatusCodes.Status423Locked,
        new ApiError(ApiErrorCodes.Locked, "Trop de tentatives. Réessayez dans quelques minutes ou appelez un responsable."));

    // ---------- Niveau 1 : compte de caisse ----------

    public async Task<(PosteSignInResponse? Response, AccessFailure? Failure)> SignInPosteAsync(
        PosteSignInRequest request, CancellationToken cancellationToken)
    {
        var login = (request.Login ?? string.Empty).Trim().ToLowerInvariant();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            """
            SELECT ra.id AS Id, ra.display_name AS DisplayName, ra.password_hash AS SecretHash,
                   ra.failed_attempts AS FailedAttempts, ra.locked_until AS LockedUntil, ra.is_active AS IsActive,
                   ra.store_id AS StoreId, s.name AS StoreName
            FROM register_accounts ra JOIN stores s ON s.id = ra.store_id
            WHERE ra.login = @login
            FOR UPDATE OF ra
            """,
            new { login },
            transaction);

        var failure = await CheckSecretAsync(
            connection, transaction, "register_accounts", account, request.Password ?? string.Empty,
            AuditActions.PosteSignInRefused,
            new AuditEntry(string.Empty, RegisterAccountId: account?.Id, StoreId: account?.StoreId, Details: new { login }));
        if (failure is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return (null, failure);
        }

        var token = Secrets.NewToken();
        await connection.ExecuteAsync(
            "INSERT INTO poste_sessions (token_hash, register_account_id, expires_at) VALUES (@hash, @id, now() + @lifetime)",
            new { hash = Secrets.TokenFingerprint(token), id = account!.Id, lifetime = PosteSessionLifetime },
            transaction);
        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(AuditActions.PosteSignedIn, RegisterAccountId: account.Id, StoreId: account.StoreId),
            transaction);
        await transaction.CommitAsync(cancellationToken);

        return (new PosteSignInResponse(token, account.DisplayName, account.StoreName), null);
    }

    // ---------- Niveau 2 : employé ----------

    public async Task<(EmployeeSignInResponse? Response, AccessFailure? Failure)> SignInEmployeeAsync(
        RequestIdentity poste, EmployeeSignInRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var employee = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            """
            SELECT id AS Id, full_name AS DisplayName, secret_hash AS SecretHash,
                   failed_attempts AS FailedAttempts, locked_until AS LockedUntil, is_active AS IsActive
            FROM employees WHERE employee_number = @number
            FOR UPDATE
            """,
            new { number = request.EmployeeNumber },
            transaction);

        var failure = await CheckSecretAsync(
            connection, transaction, "employees", employee, request.Secret ?? string.Empty,
            AuditActions.EmployeeIdentificationRefused,
            new AuditEntry(
                string.Empty, EmployeeId: employee?.Id, RegisterAccountId: poste.RegisterAccountId, StoreId: poste.StoreId,
                Details: new { matricule = request.EmployeeNumber }));
        if (failure is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return (null, failure);
        }

        // Un seul employé à la fois sur un poste : la session précédente du poste est fermée.
        await connection.ExecuteAsync(
            "UPDATE employee_sessions SET ended_at = now() WHERE poste_token_hash = @poste AND ended_at IS NULL",
            new { poste = poste.PosteTokenHash },
            transaction);

        var token = Secrets.NewToken();
        await connection.ExecuteAsync(
            """
            INSERT INTO employee_sessions (token_hash, employee_id, poste_token_hash, expires_at)
            VALUES (@hash, @id, @poste, now() + @lifetime)
            """,
            new { hash = Secrets.TokenFingerprint(token), id = employee!.Id, poste = poste.PosteTokenHash, lifetime = EmployeeSessionLifetime },
            transaction);
        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(AuditActions.EmployeeIdentified, employee.Id, poste.RegisterAccountId, poste.StoreId),
            transaction);
        var permissions = await ReadEffectivePermissionsAsync(connection, employee.Id, poste.StoreId, transaction);
        await transaction.CommitAsync(cancellationToken);

        var sellableLocationId = await SiteLocations.ReadSellableAsync(connection, poste.StoreId);
        return (new EmployeeSignInResponse(token, employee.Id, employee.DisplayName, permissions, poste.StoreId, sellableLocationId), null);
    }

    public async Task SignOutEmployeeAsync(RequestIdentity identity, string employeeToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var ended = await connection.ExecuteAsync(
            "UPDATE employee_sessions SET ended_at = now() WHERE token_hash = @hash AND ended_at IS NULL",
            new { hash = Secrets.TokenFingerprint(employeeToken) });
        if (ended > 0)
        {
            await AuditLog.WriteAsync(
                connection,
                new AuditEntry(AuditActions.EmployeeSignedOut, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId));
        }
    }

    public async Task<Guid?> ReadSellableLocationAsync(Guid storeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await SiteLocations.ReadSellableAsync(connection, storeId);
    }

    public async Task<IReadOnlyList<string>> ReadEffectivePermissionsAsync(Guid employeeId, Guid storeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await ReadEffectivePermissionsAsync(connection, employeeId, storeId, null);
    }

    private static async Task<IReadOnlyList<string>> ReadEffectivePermissionsAsync(
        NpgsqlConnection connection, Guid employeeId, Guid storeId, NpgsqlTransaction? transaction) =>
        (await connection.QueryAsync<string>(
            """
            SELECT DISTINCT permission_code FROM employee_permissions
            WHERE employee_id = @employeeId AND (store_id IS NULL OR store_id = @storeId)
            ORDER BY permission_code
            """,
            new { employeeId, storeId },
            transaction)).AsList();

    // ---------- Contrôle de chaque demande ----------

    /// <summary>
    /// Vérifie les jetons de la demande. <paramref name="requireEmployee"/> exige un employé
    /// identifié ; <paramref name="permission"/> exige en plus ce droit sur le site du poste.
    /// </summary>
    public async Task<(RequestIdentity? Identity, AccessFailure? Failure)> AuthorizeAsync(
        HttpContext context, bool requireEmployee, string? permission)
    {
        var posteToken = context.Request.Headers[ApiHeaders.PosteToken].ToString();
        if (string.IsNullOrEmpty(posteToken))
        {
            return (null, PosteNotSignedIn);
        }

        var employeeToken = context.Request.Headers[ApiHeaders.EmployeeToken].ToString();
        await using var connection = await dataSource.OpenConnectionAsync(context.RequestAborted);
        var row = await connection.QuerySingleOrDefaultAsync<IdentityRow>(
            """
            SELECT ps.token_hash AS PosteTokenHash, ra.id AS RegisterAccountId, ra.display_name AS RegisterName,
                   s.id AS StoreId, s.name AS StoreName, e.id AS EmployeeId, e.full_name AS EmployeeName
            FROM poste_sessions ps
            JOIN register_accounts ra ON ra.id = ps.register_account_id AND ra.is_active
            JOIN stores s ON s.id = ra.store_id
            LEFT JOIN employee_sessions es
                   ON es.token_hash = @employeeHash AND es.poste_token_hash = ps.token_hash
                  AND es.ended_at IS NULL AND es.expires_at > now()
            LEFT JOIN employees e ON e.id = es.employee_id AND e.is_active
            WHERE ps.token_hash = @posteHash AND ps.revoked_at IS NULL AND ps.expires_at > now()
            """,
            new
            {
                posteHash = Secrets.TokenFingerprint(posteToken),
                employeeHash = string.IsNullOrEmpty(employeeToken) ? string.Empty : Secrets.TokenFingerprint(employeeToken),
            });

        if (row is null)
        {
            return (null, PosteNotSignedIn);
        }

        var identity = new RequestIdentity(
            row.PosteTokenHash, row.RegisterAccountId, row.RegisterName, row.StoreId, row.StoreName, row.EmployeeId, row.EmployeeName);

        if ((requireEmployee || permission is not null) && identity.EmployeeId is null)
        {
            return (null, EmployeeNotIdentified);
        }

        if (permission is not null)
        {
            var allowed = await connection.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (SELECT 1 FROM employee_permissions
                               WHERE employee_id = @employeeId AND permission_code = @permission
                                 AND (store_id IS NULL OR store_id = @storeId))
                """,
                new { employeeId = identity.EmployeeId, permission, storeId = identity.StoreId });
            if (!allowed)
            {
                await AuditLog.WriteAsync(
                    connection,
                    new AuditEntry(
                        AuditActions.PermissionDenied, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                        Details: new { permission, chemin = context.Request.Path.Value }));
                return (null, new AccessFailure(
                    StatusCodes.Status403Forbidden,
                    new ApiError(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action.")));
            }
        }

        return (identity, null);
    }

    private static readonly AccessFailure PosteNotSignedIn = new(
        StatusCodes.Status401Unauthorized,
        new ApiError(ApiErrorCodes.PosteNotSignedIn, "Ce poste n'est pas connecté avec un compte de caisse."));

    private static readonly AccessFailure EmployeeNotIdentified = new(
        StatusCodes.Status401Unauthorized,
        new ApiError(ApiErrorCodes.EmployeeNotIdentified, "Identifiez-vous avec votre matricule et votre code."));

    // ---------- Vérification commune d'un secret, avec verrouillage ----------

    private static async Task<AccessFailure?> CheckSecretAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        AccountRow? account,
        string secret,
        string refusedAction,
        AuditEntry auditContext)
    {
        if (account is null || !account.IsActive)
        {
            Secrets.BurnTime(secret);
            await AuditLog.WriteAsync(connection, auditContext with { Action = refusedAction }, transaction);
            return InvalidCredentials;
        }

        if (account.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow)
        {
            return Locked;
        }

        if (Secrets.Verify(secret, account.SecretHash))
        {
            await connection.ExecuteAsync(
                $"UPDATE {table} SET failed_attempts = 0, locked_until = NULL WHERE id = @Id", new { account.Id }, transaction);
            return null;
        }

        var attempts = account.FailedAttempts + 1;
        var mustLock = attempts >= MaxFailedAttempts;
        await connection.ExecuteAsync(
            $"""
             UPDATE {table}
             SET failed_attempts = @attempts, locked_until = CASE WHEN @mustLock THEN now() + @lockDuration ELSE NULL END
             WHERE id = @Id
             """,
            new { account.Id, attempts = mustLock ? 0 : attempts, mustLock, lockDuration = LockDuration },
            transaction);
        await AuditLog.WriteAsync(connection, auditContext with { Action = refusedAction }, transaction);
        if (mustLock)
        {
            await AuditLog.WriteAsync(connection, auditContext with { Action = AuditActions.AccountLocked }, transaction);
        }

        return InvalidCredentials;
    }

    private sealed class AccountRow
    {
        public Guid Id { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string SecretHash { get; init; } = string.Empty;
        public int FailedAttempts { get; init; }
        public DateTime? LockedUntil { get; init; }
        public bool IsActive { get; init; }
        public Guid? StoreId { get; init; }
        public string StoreName { get; init; } = string.Empty;
    }

    private sealed class IdentityRow
    {
        public string PosteTokenHash { get; init; } = string.Empty;
        public Guid RegisterAccountId { get; init; }
        public string RegisterName { get; init; } = string.Empty;
        public Guid StoreId { get; init; }
        public string StoreName { get; init; } = string.Empty;
        public Guid? EmployeeId { get; init; }
        public string? EmployeeName { get; init; }
    }
}
