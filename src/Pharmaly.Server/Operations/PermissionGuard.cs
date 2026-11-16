using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Access;

namespace Pharmaly.Server.Operations;

/// <summary>
/// Droits supplémentaires d'une route, quand ils dépendent du contenu de la demande
/// (remise, encaissement…). Même règle que le filtre des routes : le droit doit valoir
/// sur le site du poste ; tout refus est audité.
/// </summary>
public static class PermissionGuard
{
    /// <summary>Null si l'employé possède au moins un de ces droits ; sinon la réponse de refus.</summary>
    public static async Task<IResult?> RefuseWithoutAnyAsync(
        NpgsqlDataSource dataSource, HttpContext context, params string[] permissions)
    {
        var identity = context.GetIdentity();
        await using var connection = await dataSource.OpenConnectionAsync(context.RequestAborted);
        var allowed = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM employee_permissions
                           WHERE employee_id = @employeeId AND permission_code = ANY(@permissions)
                             AND (store_id IS NULL OR store_id = @storeId))
            """,
            new { employeeId = identity.EmployeeId, permissions, storeId = identity.StoreId });
        if (allowed)
        {
            return null;
        }

        await AuditLog.WriteAsync(
            connection,
            new AuditEntry(
                AuditActions.PermissionDenied, identity.EmployeeId, identity.RegisterAccountId, identity.StoreId,
                Details: new { permission = string.Join(" ou ", permissions), chemin = context.Request.Path.Value }));
        return Results.Json(
            new ApiError(ApiErrorCodes.PermissionDenied, "Vous n'avez pas le droit d'effectuer cette action."),
            statusCode: StatusCodes.Status403Forbidden);
    }
}
