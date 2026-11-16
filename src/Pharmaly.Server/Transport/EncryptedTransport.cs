using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Pharmaly.Contracts;

namespace Pharmaly.Server.Transport;

/// <summary>
/// Chiffrement des échanges avec les postes. Le serveur n'écoute qu'en HTTPS (TLS 1.2 au
/// minimum), avec son propre certificat. Réglages, section « Transport » :
///   HttpsPort        port d'écoute chiffré (5443 par défaut) ;
///   CertificateFile  fichier du certificat, par défaut à côté du programme ;
///   ClearHttpPort    développement seulement : rétablit une écoute HTTP non chiffrée,
///                    limitée à cette machine (127.0.0.1). Absent ou 0 : aucune écoute claire.
/// Les réglages généraux d'adresse (« Urls », ASPNETCORE_URLS, --urls) sont sans effet :
/// aucune écoute non chiffrée ne peut apparaître par ce biais.
/// </summary>
public static class EncryptedTransport
{
    public const string FingerprintArgument = "--empreinte";
    public const int DefaultHttpsPort = 5443;
    private const string DefaultCertificateFile = "certificat-serveur.pfx";
    private const string LogCategory = "Pharmaly.Server.Transport";

    public static void UseEncryptedTransport(this WebApplicationBuilder builder) =>
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            var configuration = context.Configuration;
            var logger = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
            var certificate = ServerCertificateStore.LoadOrCreate(CertificatePath(configuration));
            var httpsPort = configuration.GetValue<int?>("Transport:HttpsPort") ?? DefaultHttpsPort;

            kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = certificate,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }));
            logger.LogInformation(
                "Échanges chiffrés (HTTPS) sur le port {Port}. Empreinte du certificat à comparer sur les postes : {Fingerprint}",
                httpsPort,
                CertificateFingerprint.Format(CertificateFingerprint.Of(certificate)).Replace('\n', ' '));

            if (configuration.GetValue<int?>("Transport:ClearHttpPort") is > 0 and var clearPort)
            {
                kestrel.Listen(IPAddress.Loopback, clearPort);
                logger.LogWarning(
                    "ÉCOUTE NON CHIFFRÉE activée sur 127.0.0.1:{Port} (réglage Transport:ClearHttpPort). "
                    + "Réservée au développement : à retirer sur le serveur d'un magasin.",
                    clearPort);
            }
        });

    /// <summary>Texte affiché par <c>Pharmaly.Server.exe --empreinte</c>. Crée le certificat s'il n'existe pas encore.</summary>
    public static string DescribeFingerprint(IConfiguration configuration)
    {
        using var certificate = ServerCertificateStore.LoadOrCreate(CertificatePath(configuration));
        var fingerprint = CertificateFingerprint.Of(certificate);
        return string.Join(Environment.NewLine,
            "Empreinte du certificat de ce serveur (SHA-256).",
            "À la première connexion d'un poste, comparez-la à celle que le poste affiche :",
            string.Empty,
            CertificateFingerprint.Format(fingerprint).ReplaceLineEndings(),
            string.Empty,
            $"EMPREINTE={fingerprint}");
    }

    /// <summary>
    /// Le fichier est cherché à côté du programme et non dans le dossier courant : le service
    /// et la commande <c>--empreinte</c> lisent ainsi toujours le même certificat.
    /// </summary>
    private static string CertificatePath(IConfiguration configuration) =>
        Path.Combine(AppContext.BaseDirectory, configuration["Transport:CertificateFile"] ?? DefaultCertificateFile);
}
