using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharmaly.Domain;
using Pharmaly.Server.Maintenance;

namespace Pharmaly.Tests.Server;

public sealed class BackupTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private const string FingerprintSql =
        """
        SELECT (SELECT count(*) FROM products) || ':' ||
               (SELECT coalesce(md5(string_agg(id::text || name || sale_price::text, '|' ORDER BY id)), '') FROM products) || ':' ||
               (SELECT count(*) FROM audit_logs) || ':' ||
               (SELECT coalesce(md5(string_agg(id::text || action || details::text, '|' ORDER BY id)), '') FROM audit_logs) || ':' ||
               (SELECT count(*) FROM employees) || ':' ||
               (SELECT max(version) FROM schema_migrations)
        """;

    [Fact]
    public async Task Une_sauvegarde_restauree_dans_une_base_neuve_est_identique_a_l_origine()
    {
        var backups = server.Services.GetRequiredService<BackupService>();
        var directory = Directory.CreateTempSubdirectory("pharmaly-sauvegarde-").FullName;
        var restored = $"pharmaly_restauration_{Guid.NewGuid():N}";
        await using (var connection = await server.DataSource.OpenConnectionAsync())
        {
            for (var index = 0; index < 25; index++)
            {
                var name = $"Produit sauvegardé n°{index} — crème à 12,50";
                await connection.ExecuteAsync(
                    "INSERT INTO products (name, search_name, sale_price) VALUES (@name, @key, @price)",
                    new { name, key = TextNormalizer.ToSearchKey(name), price = 100.25m + index });
            }
        }

        try
        {
            var file = await backups.BackupAsync(directory);
            Assert.True(new FileInfo(file).Length > 0);

            await backups.RestoreAsync(file, restored);

            await using var original = await server.DataSource.OpenConnectionAsync();
            await using var copy = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = restored }.ConnectionString);
            Assert.Equal(await original.ExecuteScalarAsync<string>(FingerprintSql), await copy.ExecuteScalarAsync<string>(FingerprintSql));

            // Les protections de la base (journal en ajout seul) survivent à la restauration.
            var exception = await Assert.ThrowsAsync<PostgresException>(() => copy.ExecuteAsync("DELETE FROM audit_logs"));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, exception.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await server.DataSource.OpenConnectionAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {restored} WITH (FORCE)");
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task La_restauration_refuse_d_ecraser_la_base_en_service_ou_une_base_existante()
    {
        var backups = server.Services.GetRequiredService<BackupService>();
        var directory = Directory.CreateTempSubdirectory("pharmaly-sauvegarde-").FullName;
        try
        {
            var file = await backups.BackupAsync(directory);
            var current = new NpgsqlConnectionStringBuilder(server.ConnectionString).Database!;

            await Assert.ThrowsAsync<InvalidOperationException>(() => backups.RestoreAsync(file, current));
            await Assert.ThrowsAsync<PostgresException>(() => backups.RestoreAsync(file, "postgres"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Seules_les_sauvegardes_les_plus_recentes_sont_conservees()
    {
        var backups = server.Services.GetRequiredService<BackupService>();
        var directory = Directory.CreateTempSubdirectory("pharmaly-sauvegarde-").FullName;
        try
        {
            foreach (var stamp in new[] { "20260101-010101", "20260102-010101", "20260103-010101" })
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"pharmaly-{stamp}.dump"), "ancienne");
            }

            var newest = await backups.BackupAsync(directory, retentionCount: 2);

            var kept = Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(2, kept.Length);
            Assert.Contains(Path.GetFileName(newest), kept);
            Assert.Contains("pharmaly-20260103-010101.dump", kept);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
