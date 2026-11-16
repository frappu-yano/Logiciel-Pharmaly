using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>
/// Transport unique vers le serveur du site : jetons, en-têtes, envoi et lecture des refus.
/// Les clients par module (<see cref="AccessClient"/>, <see cref="CatalogClient"/>,
/// <see cref="AdminClient"/>…) s'appuient dessus et ne contiennent que leurs routes.
/// Ajouter un module = ajouter un fichier client ; ce fichier-ci ne change pas.
/// </summary>
public interface IServerTransport
{
    /// <summary>Vrai si le poste est connecté avec un compte de caisse (niveau 1).</summary>
    bool HasPosteToken { get; }

    /// <summary>Mémorise la connexion du poste ; elle est conservée entre deux lancements.</summary>
    void SetPosteToken(string token);

    /// <summary>Jeton de l'employé identifié (niveau 2), ou <c>null</c> quand le poste est verrouillé.</summary>
    void SetEmployeeToken(string? token);

    /// <summary>Oublie la connexion du poste et celle de l'employé.</summary>
    void ForgetTokens();

    /// <summary>
    /// Envoie une demande et lit la réponse. Un refus du serveur lève
    /// <see cref="ServerRefusedException"/> avec le code et le message français de <see cref="ApiError"/>.
    /// La même exception est levée, avec un code de <see cref="ServerTrust"/>, quand c'est le
    /// poste qui refuse de parler au serveur (serveur non approuvé, échanges non chiffrés) :
    /// rien n'a alors été envoyé.
    /// </summary>
    Task<T?> SendAsync<T>(
        HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
        where T : class;

    /// <summary>Envoie une demande dont la réponse n'a pas de contenu utile.</summary>
    Task SendAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken);

    /// <summary>
    /// Lit une réponse dont le contenu a le même format que la demande soit acceptée ou non
    /// (état du serveur : il décrit aussi une base de données indisponible).
    /// </summary>
    Task<T?> GetWhateverStatusAsync<T>(string route, CancellationToken cancellationToken)
        where T : class;
}

/// <summary>
/// Dialogue avec le serveur du site, sur le réseau interne uniquement. Les échanges sont
/// chiffrés (HTTPS, TLS 1.2 au minimum) et le serveur est reconnu à son empreinte : voir
/// <see cref="ServerTrust"/>. Une adresse en https:// ne retombe jamais en HTTP clair.
/// </summary>
public sealed class ServerTransport : IServerTransport, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private readonly HttpClient http;
    private readonly IPosteTokenStore tokenStore;
    private readonly ServerTrust? trust;
    private string? posteToken;
    private string? employeeToken;

    public ServerTransport(PosteSettings settings, IPosteTokenStore tokenStore, ServerTrust trust)
        : this(settings.ServerUrl, tokenStore, trust)
    {
    }

    public ServerTransport(Uri serverUrl, IPosteTokenStore tokenStore, ServerTrust trust)
        : this(CreateHandler(trust), serverUrl, tokenStore)
    {
        this.trust = trust;
    }

    /// <summary>Pour les tests : le transport parle à un gestionnaire HTTP fourni.</summary>
    public ServerTransport(HttpMessageHandler handler, Uri serverUrl, IPosteTokenStore tokenStore)
    {
        this.tokenStore = tokenStore;
        posteToken = tokenStore.Load();
        http = new HttpClient(handler) { BaseAddress = serverUrl, Timeout = RequestTimeout };
    }

    private static SocketsHttpHandler CreateHandler(ServerTrust trust) => new()
    {
        // Le poste ne passe jamais par un proxy : le serveur est sur le réseau du site.
        UseProxy = false,
        // Aucune redirection n'est suivie : un serveur ne peut pas envoyer le poste ailleurs,
        // ni vers une adresse non chiffrée.
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(3),
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => trust.Accepts(certificate),
        },
    };

    public bool HasPosteToken => posteToken is not null;

    public void SetPosteToken(string token)
    {
        posteToken = token;
        employeeToken = null;
        tokenStore.Save(token);
    }

    public void SetEmployeeToken(string? token) => employeeToken = token;

    public void ForgetTokens()
    {
        posteToken = null;
        employeeToken = null;
        tokenStore.Clear();
    }

    public async Task<T?> SendAsync<T>(
        HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
        where T : class
    {
        using var response = await SendRequestAsync(method, route, body, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response.StatusCode == HttpStatusCode.NoContent
                ? null
                : await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }

        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        throw await ReadRefusalAsync(response, cancellationToken);
    }

    public async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRequestAsync(method, route, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadRefusalAsync(response, cancellationToken);
        }
    }

    public async Task<T?> GetWhateverStatusAsync<T>(string route, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await SendRequestAsync(HttpMethod.Get, route, null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (trust?.RefusalBeforeConnecting() is { } notEncrypted)
        {
            throw notEncrypted;
        }

        using var request = new HttpRequestMessage(method, route.TrimStart('/'));
        if (posteToken is not null)
        {
            request.Headers.Add(ApiHeaders.PosteToken, posteToken);
        }

        if (employeeToken is not null)
        {
            request.Headers.Add(ApiHeaders.EmployeeToken, employeeToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception) when (IsFailedHandshake(exception) && trust?.RefusalAfterFailedHandshake() is { } notTrusted)
        {
            throw notTrusted;
        }
    }

    private static bool IsFailedHandshake(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ServerRefusedException> ReadRefusalAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = new ServerRefusedException("ERREUR", $"Le serveur a répondu {(int)response.StatusCode}.");
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken);
            return error is { Code.Length: > 0, Message.Length: > 0 }
                ? new ServerRefusedException(error.Code, error.Message)
                : fallback;
        }
        catch (System.Text.Json.JsonException)
        {
            return fallback;
        }
    }

    public void Dispose() => http.Dispose();
}
