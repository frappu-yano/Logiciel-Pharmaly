using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pharmaly.Server.Transport;

/// <summary>
/// Certificat du serveur : créé au premier démarrage, puis relu tel quel à chaque lancement.
/// Il n'y a ni Internet ni autorité de certification sur le réseau d'un magasin : le
/// certificat est auto-signé et les postes le reconnaissent à son empreinte, approuvée une
/// fois par l'installateur. Le fichier contient la clé privée : il est chiffré par Windows
/// pour cette machine (DPAPI) ; ailleurs, seul le compte du serveur peut le lire (mode 600).
/// </summary>
public static class ServerCertificateStore
{
    // Les postes ne regardent que l'empreinte : la date de fin n'a pas de rôle de sécurité,
    // elle doit seulement ne jamais interrompre un magasin.
    private const int ValidityYears = 30;
    private const int KeySizeBits = 3072;

    public static X509Certificate2 LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Create(path);
        }

        return Load(path);
    }

    private static X509Certificate2 Load(string path)
    {
        try
        {
            var stored = File.ReadAllBytes(path);
            var pfx = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, null, DataProtectionScope.LocalMachine)
                : stored;
            // Toujours relu depuis le fichier, jamais utilisé tel que créé en mémoire : sous
            // Windows, une clé qui n'a jamais été chargée ainsi ne peut pas servir à TLS.
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Jamais de remplacement silencieux : un nouveau certificat serait refusé par tous les postes.
            throw new InvalidOperationException(
                $"Le certificat du serveur ({path}) est illisible : il a été créé sur une autre machine, abîmé, "
                + "ou ce compte n'a pas le droit de le lire. Pour en créer un nouveau, supprimez ce fichier ; chaque poste "
                + "devra alors être relancé avec --oublier-serveur pour approuver la nouvelle empreinte.",
                exception);
        }
    }

    private static void Create(string path)
    {
        using var key = RSA.Create(KeySizeBits);
        var request = new CertificateRequest(
            "CN=Pharmaly Gestion - Serveur", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        // Nom de pure forme (les postes ne regardent que l'empreinte) : il évite seulement
        // une remarque sans objet dans le journal du serveur.
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("pharmaly-serveur");
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(ValidityYears));

        var pfx = certificate.Export(X509ContentType.Pfx);
        var stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(pfx, null, DataProtectionScope.LocalMachine)
            : pfx;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        // Écrit à côté puis mis en place d'un seul geste : un autre lancement du serveur
        // (« --empreinte » pendant que le service démarre) ne lit jamais un fichier incomplet
        // et ne remplace jamais un certificat déjà en place.
        var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var file = new FileStream(temporary, options))
            {
                file.Write(stored);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Un autre lancement vient de créer le certificat : c'est le sien qui sert.
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
