using System.Security.Cryptography;
using System.Text;

namespace Pharmaly.Poste.Services;

public interface IPosteTokenStore
{
    string? Load();

    void Save(string token);

    void Clear();
}

/// <summary>
/// Conserve la connexion du poste (compte de caisse) entre deux lancements. Sous Windows,
/// le jeton est chiffré par le système pour l'utilisateur Windows courant.
/// </summary>
public sealed class PosteTokenStore : IPosteTokenStore
{
    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmalyGestion", "poste.session");

    public string? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var stored = File.ReadAllBytes(path);
            var clear = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                : stored;
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
        {
            // Fichier illisible ou chiffré par un autre utilisateur : le poste redemande le compte de caisse.
            return null;
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clear = Encoding.UTF8.GetBytes(token);
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
