using System.Data;
using System.Text.Json;
using Dapper;

namespace Pharmaly.Server.Operations;

/// <summary>
/// Lecture d'objets composés (ticket avec ses lignes, son paiement…) : la requête construit
/// le JSON avec json_build_object et il est désérialisé dans le contrat. Les montants
/// « numeric » restent des <see cref="decimal"/> exacts, les dates gardent leur fuseau.
/// </summary>
public static class SqlJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Une ligne au plus, dont l'unique colonne est un texte JSON.</summary>
    public static async Task<T?> ReadSingleAsync<T>(
        IDbConnection connection, string sql, object? parameters = null, IDbTransaction? transaction = null)
        where T : class
    {
        var json = await connection.QuerySingleOrDefaultAsync<string?>(sql, parameters, transaction);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>Une ligne par objet, dans l'ordre de la requête.</summary>
    public static async Task<List<T>> ReadListAsync<T>(
        IDbConnection connection, string sql, object? parameters = null, IDbTransaction? transaction = null)
        where T : class =>
        (await connection.QueryAsync<string>(sql, parameters, transaction))
            .Select(json => JsonSerializer.Deserialize<T>(json, Options)!)
            .ToList();
}
