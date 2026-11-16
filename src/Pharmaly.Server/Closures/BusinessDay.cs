using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Closures;

/// <summary>
/// La « date du jour » d'un magasin. Seul endroit où elle est calculée : heure d'Algérie,
/// UTC+1 toute l'année (pas de changement d'heure). L'horloge est injectée, pour les tests.
/// </summary>
public sealed class StoreClock(TimeProvider timeProvider)
{
    public static readonly TimeSpan AlgeriaOffset = TimeSpan.FromHours(1);

    public DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().ToOffset(AlgeriaOffset).DateTime);
}

/// <summary>
/// Verrou de la journée d'un magasin, tenu jusqu'à la fin de la transaction.
/// <para>
/// La clôture le prend en exclusif : pendant qu'elle fige la journée, rien ne peut s'y ajouter.
/// Les opérations rattachées à une session de caisse ouverte n'en ont pas besoin (la clôture
/// est refusée tant qu'une caisse est ouverte, et elles verrouillent leur session). Celles qui
/// n'exigent pas de session ouverte — ouverture d'une caisse, annulation d'une vente — le
/// prennent en partage, AVANT tout autre verrou.
/// </para>
/// </summary>
public static class BusinessDayLock
{
    private const string KeySql = "hashtextextended('pharmaly.journee:' || @storeId::text, 0)";

    public static Task SharedAsync(OperationContext operation, Guid storeId) =>
        operation.Connection.ExecuteAsync(
            $"SELECT pg_advisory_xact_lock_shared({KeySql})", new { storeId }, operation.Transaction);

    public static Task ExclusiveAsync(OperationContext operation, Guid storeId) =>
        operation.Connection.ExecuteAsync(
            $"SELECT pg_advisory_xact_lock({KeySql})", new { storeId }, operation.Transaction);
}

/// <summary>
/// Opérations de secours (hors ligne) connues du magasin mais pas encore synchronisées.
/// <para>
/// Le mode secours n'est pas développé (décision réservée à la direction) : rien n'écrit dans
/// « offline_pending_operations », le compteur vaut donc toujours zéro aujourd'hui. Il n'existe
/// que pour le garde-fou de la clôture, et c'est le seul endroit qui le lit.
/// </para>
/// </summary>
public static class OfflineOperations
{
    public static Task<int> CountPendingAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId) =>
        connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM offline_pending_operations WHERE store_id = @storeId AND synced_at IS NULL",
            new { storeId },
            transaction);
}

/// <summary>Refus d'une fermeture de caisse ou d'une clôture, avec la liste de ce qui bloque.</summary>
public sealed class ClosureBlockedException(string code, string message, IReadOnlyList<ClosureBlocker> blockers)
    : BusinessException(code, message, StatusCodes.Status409Conflict)
{
    public IReadOnlyList<ClosureBlocker> Blockers { get; } = blockers;

    public override object ToErrorBody() => new ClosureBlockedError(Code, Message, Blockers);
}
