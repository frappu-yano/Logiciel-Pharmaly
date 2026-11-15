using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Server.Access;

/// <summary>Administration des employés et de leurs droits. Tout changement est audité.</summary>
public static class EmployeeAdminEndpoints
{
    public const int MinSecretLength = 4;
    private const int MaxSecretLength = 64;
    private const int MaxNameLength = 100;

    public static void MapEmployeeAdminEndpoints(this WebApplication app)
    {
        app.MapGet(ApiRoutes.Employees, ListAsync).RequirePermission(Permissions.UsersAdminister);
        app.MapPost(ApiRoutes.Employees, CreateAsync).RequirePermission(Permissions.UsersAdminister);
        app.MapPut(ApiRoutes.Employees + "/{id:guid}/permissions", SetPermissionsAsync).RequirePermission(Permissions.UsersAdminister);
        app.MapPut(ApiRoutes.Employees + "/{id:guid}/etat", SetStateAsync).RequirePermission(Permissions.UsersAdminister);
        app.MapPut(ApiRoutes.Employees + "/{id:guid}/code", ResetSecretAsync).RequirePermission(Permissions.UsersAdminister);
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: statusCode);

    private static async Task<IResult> ListAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var employees = (await connection.QueryAsync<(Guid Id, int Number, string Name, bool IsActive)>(
            "SELECT id, employee_number, full_name, is_active FROM employees ORDER BY full_name")).AsList();
        var grants = (await connection.QueryAsync<(Guid EmployeeId, string Code, Guid? StoreId)>(
            "SELECT employee_id, permission_code, store_id FROM employee_permissions ORDER BY permission_code"))
            .ToLookup(grant => grant.EmployeeId, grant => new PermissionGrant(grant.Code, grant.StoreId));

        return Results.Ok(employees.Select(employee =>
            new EmployeeSummary(employee.Id, employee.Number, employee.Name, employee.IsActive, grants[employee.Id].ToList())));
    }

    private static async Task<IResult> CreateAsync(CreateEmployeeRequest request, HttpContext context, NpgsqlDataSource dataSource)
    {
        var name = (request.FullName ?? string.Empty).Trim();
        if (request.EmployeeNumber <= 0 || name.Length is 0 or > MaxNameLength || !IsAcceptableSecret(request.Secret))
        {
            return Error(
                StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest,
                $"Matricule, nom et code personnel ({MinSecretLength} caractères minimum) sont obligatoires.");
        }

        var identity = context.GetIdentity();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var id = await connection.ExecuteScalarAsync<Guid>(
                "INSERT INTO employees (employee_number, full_name, secret_hash) VALUES (@number, @name, @hash) RETURNING id",
                new { number = request.EmployeeNumber, name, hash = Secrets.Hash(request.Secret) },
                transaction);
            await AuditLog.WriteAsync(
                connection,
                new AuditEntry(
                    AuditActions.EmployeeCreated, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                    "employee", id.ToString(), new { matricule = request.EmployeeNumber, nom = name }),
                transaction);
            await transaction.CommitAsync();
            return Results.Ok(new EmployeeSummary(id, request.EmployeeNumber, name, true, []));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Error(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Ce matricule est déjà attribué.");
        }
    }

    private static async Task<IResult> SetPermissionsAsync(
        Guid id, SetPermissionsRequest request, HttpContext context, NpgsqlDataSource dataSource)
    {
        var identity = context.GetIdentity();
        if (id == identity.EmployeeId)
        {
            return Error(
                StatusCodes.Status403Forbidden, ApiErrorCodes.SelfModificationForbidden,
                "Vous ne pouvez pas modifier vos propres droits.");
        }

        var requested = (request.Permissions ?? []).Distinct().ToList();
        if (requested.Any(grant => !Permissions.IsKnown(grant.Code)))
        {
            return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "Un des droits demandés n'existe pas.");
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (await connection.ExecuteScalarAsync<Guid?>("SELECT id FROM employees WHERE id = @id FOR UPDATE", new { id }, transaction) is null)
        {
            return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Employé introuvable.");
        }

        var before = (await connection.QueryAsync<(string Code, Guid? StoreId)>(
            "SELECT permission_code, store_id FROM employee_permissions WHERE employee_id = @id", new { id }, transaction))
            .Select(grant => new PermissionGrant(grant.Code, grant.StoreId)).ToList();

        await connection.ExecuteAsync("DELETE FROM employee_permissions WHERE employee_id = @id", new { id }, transaction);
        try
        {
            foreach (var grant in requested)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO employee_permissions (employee_id, permission_code, store_id, granted_by)
                    VALUES (@id, @Code, @StoreId, @grantedBy)
                    """,
                    new { id, grant.Code, grant.StoreId, grantedBy = identity.EmployeeId },
                    transaction);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Error(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, "Un des sites indiqués n'existe pas.");
        }

        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.PermissionsChanged, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                "employee", id.ToString(),
                new { ajoutes = requested.Except(before).ToList(), retires = before.Except(requested).ToList() }),
            transaction);
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> SetStateAsync(Guid id, SetEmployeeStateRequest request, HttpContext context, NpgsqlDataSource dataSource)
    {
        var identity = context.GetIdentity();
        if (id == identity.EmployeeId)
        {
            return Error(
                StatusCodes.Status403Forbidden, ApiErrorCodes.SelfModificationForbidden,
                "Vous ne pouvez pas désactiver votre propre compte.");
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var updated = await connection.ExecuteAsync(
            "UPDATE employees SET is_active = @IsActive, updated_at = now() WHERE id = @id", new { id, request.IsActive }, transaction);
        if (updated == 0)
        {
            return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Employé introuvable.");
        }

        if (!request.IsActive)
        {
            await connection.ExecuteAsync(
                "UPDATE employee_sessions SET ended_at = now() WHERE employee_id = @id AND ended_at IS NULL", new { id }, transaction);
        }

        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.EmployeeStateChanged, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                "employee", id.ToString(), new { actif = request.IsActive }),
            transaction);
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> ResetSecretAsync(Guid id, ResetSecretRequest request, HttpContext context, NpgsqlDataSource dataSource)
    {
        if (!IsAcceptableSecret(request.Secret))
        {
            return Error(
                StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest,
                $"Le code personnel doit contenir au moins {MinSecretLength} caractères.");
        }

        var identity = context.GetIdentity();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var updated = await connection.ExecuteAsync(
            """
            UPDATE employees SET secret_hash = @hash, failed_attempts = 0, locked_until = NULL, updated_at = now()
            WHERE id = @id
            """,
            new { id, hash = Secrets.Hash(request.Secret) },
            transaction);
        if (updated == 0)
        {
            return Error(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "Employé introuvable.");
        }

        // Les sessions ouvertes avec l'ancien code sont fermées.
        await connection.ExecuteAsync(
            "UPDATE employee_sessions SET ended_at = now() WHERE employee_id = @id AND ended_at IS NULL", new { id }, transaction);
        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.SecretReset, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId, "employee", id.ToString()),
            transaction);
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static bool IsAcceptableSecret(string? secret) =>
        secret is { Length: >= MinSecretLength and <= MaxSecretLength };
}
