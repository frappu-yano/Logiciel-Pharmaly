using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Pharmaly.Server.Database;

/// <summary>
/// Applique les scripts SQL embarqués, dans l'ordre, une seule fois chacun.
/// Chaque script s'exécute dans sa propre transaction ; un verrou consultatif empêche
/// deux serveurs de migrer la même base en même temps.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    private const long AdvisoryLockKey = 7_420_150_011;
    private const string ResourceMarker = ".Database.Migrations.";

    public async Task<int> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey });
        try
        {
            await connection.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    integer PRIMARY KEY,
                    name       text NOT NULL,
                    checksum   text NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """);

            var applied = (await connection.QueryAsync<(int Version, string Checksum)>(
                "SELECT version, checksum FROM schema_migrations")).ToDictionary(row => row.Version, row => row.Checksum);

            var appliedCount = 0;
            foreach (var migration in LoadMigrations())
            {
                if (applied.TryGetValue(migration.Version, out var knownChecksum))
                {
                    if (knownChecksum != migration.Checksum)
                    {
                        throw new InvalidOperationException(
                            $"La migration {migration.Version} ({migration.Name}) a été modifiée après son application.");
                    }

                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(migration.Sql, transaction: transaction);
                await connection.ExecuteAsync(
                    "INSERT INTO schema_migrations (version, name, checksum) VALUES (@Version, @Name, @Checksum)",
                    migration,
                    transaction);
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation("Migration {Version} appliquée : {Name}", migration.Version, migration.Name);
                appliedCount++;
            }

            return appliedCount;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    public static async Task<int> ReadSchemaVersionAsync(NpgsqlConnection connection) =>
        await connection.ExecuteScalarAsync<int?>("SELECT max(version) FROM schema_migrations") ?? 0;

    private static IReadOnlyList<Migration> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(resourceName =>
            {
                var fileName = resourceName[(resourceName.IndexOf(ResourceMarker, StringComparison.Ordinal) + ResourceMarker.Length)..];
                var separator = fileName.IndexOf('_');
                var version = int.Parse(fileName[..separator]);
                var name = fileName[(separator + 1)..^".sql".Length];

                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                // Les fins de ligne sont normalisées pour que la somme de contrôle soit la même sous Windows et Linux.
                var sql = reader.ReadToEnd().ReplaceLineEndings("\n");
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                return new Migration(version, name, sql, checksum);
            })
            .OrderBy(migration => migration.Version)
            .ToList();
    }

    private sealed record Migration(int Version, string Name, string Sql, string Checksum);
}
