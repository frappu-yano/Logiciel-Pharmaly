using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Access;

namespace Pharmaly.Server.Operations;

/// <summary>Ce dont une commande d'écriture dispose : la transaction unique et l'identité.</summary>
public sealed record OperationContext(
    NpgsqlConnection Connection,
    NpgsqlTransaction Transaction,
    Guid OperationId,
    RequestIdentity Identity)
{
    public Guid EmployeeId => Identity.RequireEmployeeId();

    /// <summary>Entrée d'audit déjà renseignée avec l'employé, la caisse et le site.</summary>
    public Task AuditAsync(string action, string entityType, string entityId, object details) =>
        AuditLog.WriteAsync(
            Connection,
            new AuditEntry(action, Identity.EmployeeId, Identity.RegisterAccountId, Identity.StoreId, entityType, entityId, details),
            Transaction);
}

/// <summary>
/// Exécute une commande d'écriture dans UNE transaction, avec idempotence.
/// <para>
/// L'identifiant d'opération est inscrit dans « operations » au début de la transaction.
/// S'il existe déjà, le résultat enregistré est renvoyé et rien n'est refait. Si une autre
/// transaction est en train de traiter le même identifiant, l'inscription attend sa fin :
/// validée, on renvoie son résultat ; annulée, la commande s'exécute normalement.
/// </para>
/// <para>
/// Une commande refusée (stock insuffisant…) n'enregistre rien : la rejouer la retente.
/// </para>
/// </summary>
public sealed class OperationRunner(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IResult> RunAsync<TRequest, TResult>(
        Guid operationId,
        string kind,
        RequestIdentity identity,
        TRequest request,
        Func<OperationContext, Task<TResult>> action)
    {
        if (operationId == Guid.Empty)
        {
            return Failure(BusinessException.Invalid("Identifiant d'opération manquant."));
        }

        var requestHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + JsonSerializer.Serialize(request, JsonOptions))));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Attend ici si une transaction concurrente porte le même identifiant.
            var registered = await connection.ExecuteAsync(
                """
                INSERT INTO operations (operation_id, kind, employee_id, request_hash)
                VALUES (@operationId, @kind, @employeeId, @requestHash)
                ON CONFLICT (operation_id) DO NOTHING
                """,
                new { operationId, kind, employeeId = identity.EmployeeId, requestHash },
                transaction);

            if (registered == 0)
            {
                var known = await connection.QuerySingleAsync<(string Kind, string RequestHash, string? Result)>(
                    "SELECT kind, request_hash, result::text FROM operations WHERE operation_id = @operationId",
                    new { operationId },
                    transaction);
                await transaction.RollbackAsync();

                if (known.Kind != kind || known.RequestHash != requestHash || known.Result is null)
                {
                    return Failure(new BusinessException(
                        ApiErrorCodes.OperationMismatch,
                        "Cet identifiant d'opération a déjà servi pour une autre demande, qui reste enregistrée telle quelle. "
                        + "La demande actuelle n'a pas été exécutée.",
                        StatusCodes.Status409Conflict));
                }

                return Results.Ok(JsonSerializer.Deserialize<TResult>(known.Result, JsonOptions));
            }

            var result = await action(new OperationContext(connection, transaction, operationId, identity));

            await connection.ExecuteAsync(
                "UPDATE operations SET result = @result::jsonb WHERE operation_id = @operationId",
                new { operationId, result = JsonSerializer.Serialize(result, JsonOptions) },
                transaction);
            await transaction.CommitAsync();
            return Results.Ok(result);
        }
        catch (BusinessException exception)
        {
            await transaction.RollbackAsync();
            return Failure(exception);
        }
    }

    private static IResult Failure(BusinessException exception) =>
        Results.Json(exception.ToErrorBody(), statusCode: exception.StatusCode);
}
