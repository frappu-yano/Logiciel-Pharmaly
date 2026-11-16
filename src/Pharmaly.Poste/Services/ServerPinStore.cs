using System.Security.Cryptography;
using System.Text;

namespace Pharmaly.Poste.Services;

public interface IServerPinStore
{
    /// <summary>Empreinte du serveur approuvé sur ce poste, ou <c>null</c> si aucun ne l'a été.</summary>
    string? Load();

    void Save(string fingerprint);

    void Clear();
}

/// <summary>
/// Conserve l'empreinte du serveur approuvé par l'installateur. Elle est protégée comme la
/// connexion du poste : chiffrée par Windows pour l'utilisateur courant ; ailleurs, lisible
/// par lui seul.
/// </summary>
public sealed class ServerPinStore : IServerPinStore
{
    /// <summary>
    /// Renvoyé quand le fichier existe mais ne peut pas être relu. Ce n'est pas une empreinte :
    /// aucun serveur ne lui correspond, donc tout est refusé jusqu'à <c>--oublier-serveur</c>.
    /// Un fichier abîmé ne ramène jamais le poste à « aucun serveur approuvé ».
    /// </summary>
    public const string Unreadable = "ILLISIBLE";

    private readonly string path;

    public ServerPinStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmalyGestion", "serveur.empreinte"))
    {
    }

    public ServerPinStore(string path) => this.path = path;

    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = File.ReadAllBytes(path);
            var clear = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                : stored;
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return Unreadable;
        }
    }

    public void Save(string fingerprint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clear = Encoding.UTF8.GetBytes(fingerprint);
        var stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)
            : clear;
        File.WriteAllBytes(path, stored);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
