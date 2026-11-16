using System.Globalization;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>
/// Module « clôture quotidienne » du magasin du poste : état de la journée, clôture, historique.
/// La fermeture d'une caisse (comptage des espèces) est dans <see cref="ICashSessionClient"/>.
/// </summary>
public interface IClosuresClient
{
    /// <summary>Journée en cours : caisses, ce qui bloque la clôture, aperçu non figé des totaux (droit « consulter les rapports de caisse »).</summary>
    Task<CurrentDayStatus> GetCurrentDayAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clôture la journée du magasin et renvoie le rapport figé. Rejouer la même demande (même
    /// <c>OperationId</c>) renvoie la clôture déjà enregistrée sans rien refaire.
    /// </summary>
    Task<DailyClosureDetail> CloseDayAsync(CloseDayRequest request, CancellationToken cancellationToken);

    /// <summary>Clôtures passées du magasin, la plus récente d'abord.</summary>
    Task<IReadOnlyList<DailyClosureSummary>> GetClosuresAsync(DateOnly? from, DateOnly? to, int? limit, CancellationToken cancellationToken);

    /// <summary>Une clôture et son rapport figé ; <c>null</c> si elle n'existe pas.</summary>
    Task<DailyClosureDetail?> GetClosureAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class ClosuresClient(IServerTransport transport) : IClosuresClient
{
    public async Task<CurrentDayStatus> GetCurrentDayAsync(CancellationToken cancellationToken) =>
        (await transport.SendAsync<CurrentDayStatus>(HttpMethod.Get, ClosureRoutes.CurrentDay, null, cancellationToken))!;

    public async Task<DailyClosureDetail> CloseDayAsync(CloseDayRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<DailyClosureDetail>(HttpMethod.Post, ClosureRoutes.Closures, request, cancellationToken))!;

    public async Task<IReadOnlyList<DailyClosureSummary>> GetClosuresAsync(
        DateOnly? from, DateOnly? to, int? limit, CancellationToken cancellationToken) =>
        await transport.SendAsync<List<DailyClosureSummary>>(
            HttpMethod.Get, ClosureRoutes.Closures + ToQueryString(from, to, limit), null, cancellationToken) ?? [];

    public Task<DailyClosureDetail?> GetClosureAsync(Guid id, CancellationToken cancellationToken) =>
        transport.SendAsync<DailyClosureDetail>(HttpMethod.Get, ClosureRoutes.Closure(id), null, cancellationToken, notFoundIsNull: true);

    /// <summary>Paramètres attendus par le serveur : du, au, limite.</summary>
    public static string ToQueryString(DateOnly? from, DateOnly? to, int? limit)
    {
        var parameters = new List<string>();
        if (from is { } first)
        {
            parameters.Add("du=" + first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (to is { } last)
        {
            parameters.Add("au=" + last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (limit is { } count)
        {
            parameters.Add("limite=" + count.ToString(CultureInfo.InvariantCulture));
        }

        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
