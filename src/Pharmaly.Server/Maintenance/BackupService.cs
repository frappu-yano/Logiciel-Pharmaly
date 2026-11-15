using System.Diagnostics;
using Npgsql;

namespace Pharmaly.Server.Maintenance;

/// <summary>
/// Sauvegarde et restauration de la base avec les outils de PostgreSQL (pg_dump, pg_restore).
/// Une restauration se fait toujours dans une base NEUVE : la base en service n'est jamais écrasée.
/// </summary>
public sealed class BackupService(NpgsqlDataSource dataSource, IConfiguration configuration, ILogger<BackupService> logger)
{
    public const int DefaultRetentionCount = 30;
    private const string FilePrefix = "pharmaly-";
    private const string FileExtension = ".dump";

    /// <returns>Le chemin du fichier de sauvegarde créé et vérifié.</returns>
    public async Task<string> BackupAsync(string directory, int retentionCount = DefaultRetentionCount)
    {
        Directory.CreateDirectory(directory);
        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        var password = ReadPassword();
        var finalPath = Path.Combine(directory, $"{FilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}{FileExtension}");
        var temporaryPath = finalPath + ".encours";

        await RunAsync("pg_dump", password, ConnectionArguments(connection, connection.Database!),
            "--format=custom", "--no-owner", "--no-privileges", "--file", temporaryPath);

        // Une sauvegarde illisible ne vaut rien : son contenu est relu avant d'être conservée.
        await RunAsync("pg_restore", password, [], "--list", temporaryPath);
        File.Move(temporaryPath, finalPath, overwrite: true);
        logger.LogInformation("Sauvegarde vérifiée : {Path}", finalPath);

        foreach (var old in Directory.GetFiles(directory, $"{FilePrefix}*{FileExtension}")
                     .OrderByDescending(path => path, StringComparer.Ordinal)
                     .Skip(Math.Max(retentionCount, 1)))
        {
            File.Delete(old);
        }

        return finalPath;
    }

    /// <summary>Restaure une sauvegarde dans une base neuve, créée pour l'occasion.</summary>
    public async Task RestoreAsync(string backupPath, string targetDatabase)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Fichier de sauvegarde introuvable.", backupPath);
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(targetDatabase, "^[a-z][a-z0-9_]{2,62}$"))
        {
            throw new ArgumentException("Nom de base invalide : lettres minuscules, chiffres et tirets bas uniquement.");
        }

        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        if (string.Equals(connection.Database, targetDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La restauration ne se fait jamais dans la base en service : indiquez une base neuve.");
        }

        var password = ReadPassword();
        await using (var admin = await dataSource.OpenConnectionAsync())
        {
            // Échoue si la base existe déjà : rien n'est jamais écrasé.
            await using var create = new NpgsqlCommand($"CREATE DATABASE {targetDatabase}", admin);
            await create.ExecuteNonQueryAsync();
        }

        await RunAsync("pg_restore", password, ConnectionArguments(connection, targetDatabase),
            "--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction", backupPath);
        logger.LogInformation("Sauvegarde restaurée dans la base {Database}", targetDatabase);
    }

    private string ReadPassword() =>
        new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Pharmaly")
            ?? ReadLocalConnectionString()
            ?? dataSource.ConnectionString).Password ?? string.Empty;

    private static string? ReadLocalConnectionString() =>
        new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Local.json", optional: true).Build().GetConnectionString("Pharmaly")
        ?? new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Local.json", optional: true).Build().GetConnectionString("Pharmaly");

    private static string[] ConnectionArguments(NpgsqlConnectionStringBuilder connection, string database) =>
    [
        "--host", connection.Host ?? "localhost",
        "--port", connection.Port.ToString(),
        "--username", connection.Username ?? "postgres",
        "--dbname", database,
        "--no-password",
    ];

    private async Task RunAsync(string tool, string password, string[] connectionArguments, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveTool(tool),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in connectionArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Le mot de passe passe par l'environnement du processus, jamais par la ligne de commande.
        startInfo.Environment["PGPASSWORD"] = password;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Impossible de lancer {tool}.");
        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} a échoué (code {process.ExitCode}) : {await errors}");
        }
    }

    /// <summary>Cherche l'outil dans le dossier configuré, puis PGBIN, puis le PATH.</summary>
    private string ResolveTool(string tool)
    {
        var executable = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        foreach (var directory in new[] { configuration["Postgres:BinDirectory"], Environment.GetEnvironmentVariable("PGBIN") })
        {
            if (!string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, executable)))
            {
                return Path.Combine(directory, executable);
            }
        }

        return executable;
    }
}
