using System.Net;
using System.Security.Cryptography.X509Certificates;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Ce que l'écran de connexion a besoin de savoir pour faire approuver un serveur.</summary>
public interface IServerTrust
{
    /// <summary>Vrai tant qu'aucun serveur n'a été approuvé sur ce poste : rien ne peut être envoyé.</summary>
    bool ApprovalNeeded { get; }

    /// <summary>Empreinte du dernier serveur joint et refusé (pas encore approuvé, ou différent du serveur approuvé).</summary>
    string? PresentedFingerprint { get; }

    /// <summary>L'installateur a comparé cette empreinte à celle du serveur : elle est enregistrée.</summary>
    void Approve(string fingerprint);
}

/// <summary>
/// Reconnaissance du serveur par son empreinte (« épinglage »). Il n'y a pas d'autorité de
/// certification sur le réseau d'un magasin et les postes se connectent par adresse IP :
/// ni les autorités de Windows ni le nom du serveur ne sont consultés. Un serveur est accepté
/// si et seulement si son certificat a l'empreinte approuvée sur ce poste. Tant que ce n'est
/// pas le cas, la connexion est coupée pendant la négociation TLS, avant tout envoi : aucun
/// mot de passe, code ni jeton ne part vers un serveur non approuvé.
/// </summary>
public sealed class ServerTrust : IServerTrust
{
    /// <summary>Code de refus : aucun serveur n'a encore été approuvé sur ce poste.</summary>
    public const string ApprovalNeededCode = "SERVEUR_A_APPROUVER";

    /// <summary>Code de refus : le serveur joint n'est pas celui qui a été approuvé.</summary>
    public const string NotApprovedCode = "SERVEUR_NON_APPROUVE";

    /// <summary>Code de refus : l'adresse du serveur demande des échanges non chiffrés.</summary>
    public const string ClearHttpCode = "ECHANGES_NON_CHIFFRES";

    public const string ApprovalNeededMessage =
        "Ce serveur n'a pas encore été approuvé sur ce poste. Rien ne lui a été envoyé.";

    public const string NotApprovedMessage =
        "Le serveur n'est pas celui qui a été approuvé pour ce poste : la connexion est refusée et rien ne lui a été "
        + "envoyé. Prévenez la personne qui a installé le logiciel.";

    public const string ClearHttpMessage =
        "L'adresse du serveur doit commencer par https:// : ce poste refuse les échanges non chiffrés. "
        + "Prévenez la personne qui a installé le logiciel.";

    private readonly object gate = new();
    private readonly IServerPinStore store;
    private readonly bool encrypted;
    private readonly bool clearHttpAllowed;
    private string? approved;
    private string? presented;
    private bool lastCertificateRejected;

    /// <param name="serverUrl">Adresse réglée sur ce poste.</param>
    /// <param name="store">Empreinte approuvée, conservée entre deux lancements.</param>
    /// <param name="fingerprintForThisRun">
    /// Essai automatique seulement : empreinte attendue, fournie par celui qui lance l'essai.
    /// Elle remplace l'empreinte enregistrée pour ce lancement, sans y toucher. Un texte qui
    /// n'est pas une empreinte ne correspond à aucun serveur : tout est refusé.
    /// </param>
    public ServerTrust(Uri serverUrl, IServerPinStore store, string? fingerprintForThisRun = null)
    {
        this.store = store;
        encrypted = serverUrl.Scheme == Uri.UriSchemeHttps;
        // Sans chiffrement, seul un serveur sur cette même machine est accepté (développement) :
        // rien ne circule alors sur le réseau.
        clearHttpAllowed = serverUrl.Scheme == Uri.UriSchemeHttp && IsThisMachine(serverUrl);
        approved = fingerprintForThisRun is null
            ? store.Load()
            : CertificateFingerprint.Parse(fingerprintForThisRun) ?? ServerPinStore.Unreadable;
    }

    public bool ApprovalNeeded
    {
        get
        {
            lock (gate)
            {
                return encrypted && approved is null;
            }
        }
    }

    public string? PresentedFingerprint
    {
        get
        {
            lock (gate)
            {
                return presented;
            }
        }
    }

    public void Approve(string fingerprint)
    {
        var parsed = CertificateFingerprint.Parse(fingerprint)
            ?? throw new FormatException("Ce texte n'est pas une empreinte de certificat.");
        lock (gate)
        {
            store.Save(parsed);
            approved = parsed;
            presented = null;
        }
    }

    /// <summary>
    /// Décision prise pendant la négociation TLS. Les erreurs de chaîne et de nom signalées
    /// par le système sont volontairement ignorées : seule l'empreinte compte.
    /// </summary>
    public bool Accepts(X509Certificate? certificate)
    {
        lock (gate)
        {
            var fingerprint = certificate is null ? null : CertificateFingerprint.Of(certificate);
            var accepted = fingerprint is not null && approved == fingerprint;
            presented = accepted ? null : fingerprint;
            lastCertificateRejected = !accepted;
            return accepted;
        }
    }

    /// <summary>Refus à opposer avant même d'ouvrir une connexion, ou <c>null</c> si l'adresse est acceptable.</summary>
    public ServerRefusedException? RefusalBeforeConnecting() =>
        encrypted || clearHttpAllowed ? null : new ServerRefusedException(ClearHttpCode, ClearHttpMessage);

    /// <summary>Après un échec de la négociation TLS : le refus à afficher si c'est ce poste qui a écarté le serveur.</summary>
    public ServerRefusedException? RefusalAfterFailedHandshake()
    {
        lock (gate)
        {
            if (!lastCertificateRejected)
            {
                return null;
            }

            return approved is null
                ? new ServerRefusedException(ApprovalNeededCode, ApprovalNeededMessage)
                : new ServerRefusedException(NotApprovedCode, NotApprovedMessage);
        }
    }

    private static bool IsThisMachine(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.Host, out var address) && IPAddress.IsLoopback(address));
}
