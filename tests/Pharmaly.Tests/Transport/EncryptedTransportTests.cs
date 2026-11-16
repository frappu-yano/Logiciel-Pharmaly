using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.Services.Server;
using Pharmaly.Server.Transport;
using Pharmaly.Tests.Server;

namespace Pharmaly.Tests.Transport;

/// <summary>
/// Le vrai serveur écoute en HTTPS sur un vrai port, et le vrai transport du poste s'y
/// connecte : chiffrement, reconnaissance du serveur par son empreinte, aucun repli en clair.
/// </summary>
public sealed class EncryptedTransportTests(ServerFixture database) : IClassFixture<ServerFixture>, IDisposable
{
    private const string OtherFingerprint = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    private readonly string directory = Directory.CreateTempSubdirectory("pharmaly-transport-").FullName;
    private readonly MemoryPinStore pinStore = new();
    private readonly MemoryTokenStore tokenStore = new();

    private string CertificateFile => Path.Combine(directory, "certificat-serveur.pfx");

    private HttpsServer StartServer(Dictionary<string, string?>? moreSettings = null) =>
        HttpsServer.Start(database.ConnectionString, CertificateFile, moreSettings);

    private ServerTransport CreateTransport(Uri url, out ServerTrust trust)
    {
        trust = new ServerTrust(url, pinStore);
        return new ServerTransport(url, tokenStore, trust);
    }

    private Task<PosteSignInResponse> SignInPosteAsync(ServerTransport transport) =>
        new AccessClient(transport).SignInPosteAsync(ServerFixture.RegisterLogin, database.RegisterPassword, CancellationToken.None);

    [Fact]
    public async Task Avec_l_empreinte_approuvee_le_poste_dialogue_en_https()
    {
        await using var server = StartServer();
        pinStore.Save(server.Fingerprint);
        using var transport = CreateTransport(server.Url, out _);

        var signedIn = await SignInPosteAsync(transport);
        var health = await new AccessClient(transport).GetHealthAsync(CancellationToken.None);

        Assert.Equal("Caisse 1", signedIn.RegisterName);
        Assert.True(health!.DatabaseReachable);
        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, request => Assert.True(request.Encrypted));
        Assert.True(server.Received.Last().WithPosteToken);
    }

    [Fact]
    public async Task Avec_une_autre_empreinte_la_connexion_est_refusee_et_rien_n_est_envoye()
    {
        await using var server = StartServer();
        pinStore.Save(OtherFingerprint);
        tokenStore.Save("jeton-du-poste");
        using var transport = CreateTransport(server.Url, out var trust);

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() => SignInPosteAsync(transport));
        var refusedAgain = await Assert.ThrowsAsync<ServerRefusedException>(() =>
            new CatalogClient(transport).SearchProductsAsync("creme", CancellationToken.None));

        Assert.Equal(ServerTrust.NotApprovedCode, refused.Code);
        Assert.StartsWith("Le serveur n'est pas celui qui a été approuvé", refused.Message);
        Assert.Equal(ServerTrust.NotApprovedCode, refusedAgain.Code);
        Assert.Empty(server.Received);
        // Le refus ne remplace rien : l'empreinte approuvée reste celle d'avant, et aucune approbation n'est proposée.
        Assert.Equal(OtherFingerprint, pinStore.Saved);
        Assert.False(trust.ApprovalNeeded);
        Assert.Equal(server.Fingerprint, trust.PresentedFingerprint);
    }

    [Fact]
    public async Task Si_le_certificat_du_serveur_change_apres_l_approbation_la_connexion_est_refusee()
    {
        string firstFingerprint;
        await using (var server = StartServer())
        {
            firstFingerprint = server.Fingerprint;
            using var transport = CreateTransport(server.Url, out var trust);
            await Assert.ThrowsAsync<ServerRefusedException>(() => SignInPosteAsync(transport));
            trust.Approve(trust.PresentedFingerprint!);
            await SignInPosteAsync(transport);
        }

        Assert.NotNull(tokenStore.Saved);
        // Le serveur est réinstallé (ou un autre se fait passer pour lui) : nouveau certificat.
        File.Delete(CertificateFile);
        await using var replaced = StartServer();
        Assert.NotEqual(firstFingerprint, replaced.Fingerprint);
        using var transportAfter = CreateTransport(replaced.Url, out var trustAfter);

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() =>
            new AccessClient(transportAfter).SignInEmployeeAsync(ServerFixture.AdminNumber, database.AdminSecret, CancellationToken.None));
        var health = await Assert.ThrowsAsync<ServerRefusedException>(() =>
            new AccessClient(transportAfter).GetHealthAsync(CancellationToken.None));

        Assert.Equal(ServerTrust.NotApprovedCode, refused.Code);
        Assert.Equal(ServerTrust.NotApprovedCode, health.Code);
        Assert.Empty(replaced.Received);
        Assert.False(trustAfter.ApprovalNeeded);
        Assert.Equal(firstFingerprint, pinStore.Saved);
    }

    [Fact]
    public async Task A_la_premiere_connexion_l_empreinte_est_proposee_et_rien_n_est_envoye_avant_l_approbation()
    {
        await using var server = StartServer();
        tokenStore.Save("jeton-d-un-lancement-precedent");
        using var transport = CreateTransport(server.Url, out var trust);
        Assert.True(trust.ApprovalNeeded);

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() => SignInPosteAsync(transport));
        await Assert.ThrowsAsync<ServerRefusedException>(() => new AccessClient(transport).GetHealthAsync(CancellationToken.None));

        Assert.Equal(ServerTrust.ApprovalNeededCode, refused.Code);
        Assert.Equal(server.Fingerprint, trust.PresentedFingerprint);
        Assert.Empty(server.Received);
        Assert.Null(pinStore.Saved);
        Assert.True(trust.ApprovalNeeded);

        trust.Approve(trust.PresentedFingerprint!);
        var signedIn = await SignInPosteAsync(transport);

        Assert.Equal("Caisse 1", signedIn.RegisterName);
        Assert.Equal(server.Fingerprint, pinStore.Saved);
        Assert.False(trust.ApprovalNeeded);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Un_poste_regle_en_https_ne_parle_pas_a_un_serveur_qui_ne_repond_qu_en_clair()
    {
        await using var clearServer = await StartSideServerAsync(certificate: null, _ => Task.CompletedTask);
        pinStore.Save(OtherFingerprint);
        tokenStore.Save("jeton-du-poste");
        using var transport = CreateTransport(new Uri($"https://127.0.0.1:{clearServer.Port}/"), out _);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => SignInPosteAsync(transport));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new AccessClient(transport).GetHealthAsync(CancellationToken.None));

        Assert.Empty(clearServer.Received);
    }

    [Fact]
    public async Task Un_poste_regle_en_https_ne_suit_pas_un_renvoi_vers_une_adresse_en_clair()
    {
        await using var clearServer = await StartSideServerAsync(certificate: null, _ => Task.CompletedTask);
        using var certificate = ServerCertificateStore.LoadOrCreate(CertificateFile);
        await using var redirectingServer = await StartSideServerAsync(certificate, context =>
        {
            context.Response.Redirect($"http://127.0.0.1:{clearServer.Port}{context.Request.Path}", permanent: false, preserveMethod: true);
            return Task.CompletedTask;
        });
        pinStore.Save(CertificateFingerprint.Of(certificate));
        using var transport = CreateTransport(new Uri($"https://127.0.0.1:{redirectingServer.Port}/"), out _);

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() => SignInPosteAsync(transport));

        Assert.Equal("Le serveur a répondu 307.", refused.Message);
        Assert.Single(redirectingServer.Received);
        Assert.Empty(clearServer.Received);
    }

    [Fact]
    public async Task Un_poste_regle_en_http_vers_une_autre_machine_refuse_d_envoyer_quoi_que_ce_soit()
    {
        var url = new Uri("http://192.0.2.10:5080/");
        using var transport = new ServerTransport(url, tokenStore, new ServerTrust(url, pinStore));

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() => SignInPosteAsync(transport));

        Assert.Equal(ServerTrust.ClearHttpCode, refused.Code);
        Assert.Contains("https://", refused.Message);
        Assert.Null(new ServerTrust(new Uri("http://127.0.0.1:5080/"), pinStore).RefusalBeforeConnecting());
        Assert.Null(new ServerTrust(new Uri("https://192.0.2.10:5443/"), pinStore).RefusalBeforeConnecting());
    }

    [Fact]
    public async Task L_ecoute_http_claire_est_absente_par_defaut()
    {
        await using var server = StartServer();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        Assert.All(server.Addresses, address => Assert.StartsWith("https://", address));
        Assert.Single(server.Addresses);
        // Une demande en clair sur le port chiffré n'obtient rien non plus.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{server.HttpsPort}{ApiRoutes.Health}"));
        Assert.Empty(server.Received);
        Assert.DoesNotContain(server.Warnings, warning => warning.Contains("NON CHIFFRÉE"));
    }

    [Fact]
    public async Task Les_reglages_generaux_d_adresse_n_ouvrent_aucune_ecoute_claire()
    {
        var port = HttpsServer.FindFreePort();
        await using var server = StartServer(new() { ["urls"] = $"http://127.0.0.1:{port}" });
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        Assert.All(server.Addresses, address => Assert.StartsWith("https://", address));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}{ApiRoutes.Health}"));
    }

    [Fact]
    public async Task Un_reglage_explicite_retablit_l_ecoute_claire_sur_cette_machine_seulement_avec_un_avertissement()
    {
        var port = HttpsServer.FindFreePort();
        await using var server = StartServer(new() { ["Transport:ClearHttpPort"] = port.ToString() });
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        using var response = await http.GetAsync($"http://127.0.0.1:{port}{ApiRoutes.Health}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"http://127.0.0.1:{port}", server.Addresses);
        Assert.Contains(server.Warnings, warning => warning.Contains("ÉCOUTE NON CHIFFRÉE") && warning.Contains(port.ToString()));
    }

    [Fact]
    public async Task Le_serveur_negocie_tls_1_2_ou_1_3_et_refuse_les_versions_plus_anciennes()
    {
        await using var server = StartServer();

        Assert.True(await NegotiateAsync(server.HttpsPort, SslProtocols.Tls12 | SslProtocols.Tls13) is SslProtocols.Tls12 or SslProtocols.Tls13);
        Assert.Equal(SslProtocols.Tls12, await NegotiateAsync(server.HttpsPort, SslProtocols.Tls12));
#pragma warning disable SYSLIB0039, CS0618 // Les anciennes versions sont demandées exprès : elles doivent échouer.
        await Assert.ThrowsAnyAsync<Exception>(() => NegotiateAsync(server.HttpsPort, SslProtocols.Tls11));
        await Assert.ThrowsAnyAsync<Exception>(() => NegotiateAsync(server.HttpsPort, SslProtocols.Tls));
#pragma warning restore SYSLIB0039, CS0618
    }

    [Fact]
    public async Task L_essai_automatique_fournit_l_empreinte_attendue_sans_rien_enregistrer()
    {
        await using var server = StartServer();
        var spoken = server.Fingerprint.ToLowerInvariant().Insert(32, " : ").Insert(8, "-");
        var trust = new ServerTrust(server.Url, pinStore, spoken);
        using var transport = new ServerTransport(server.Url, tokenStore, trust);

        await SignInPosteAsync(transport);

        Assert.False(trust.ApprovalNeeded);
        Assert.Null(pinStore.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n'importe quoi")]
    [InlineData("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF")]
    public async Task Une_empreinte_d_essai_fausse_ou_illisible_ne_laisse_passer_aucun_serveur(string given)
    {
        await using var server = StartServer();
        var trust = new ServerTrust(server.Url, pinStore, given);
        using var transport = new ServerTransport(server.Url, tokenStore, trust);

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() => SignInPosteAsync(transport));

        Assert.Equal(ServerTrust.NotApprovedCode, refused.Code);
        Assert.False(trust.ApprovalNeeded);
        Assert.Empty(server.Received);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static async Task<SslProtocols> NegotiateAsync(int port, SslProtocols offered)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", EnabledSslProtocols = offered });
        return tls.SslProtocol;
    }

    /// <summary>
    /// Petit serveur d'appoint sur cette machine : en clair si <paramref name="certificate"/>
    /// est absent, sinon en HTTPS. Il note les chemins des demandes qu'il reçoit.
    /// </summary>
    private static async Task<SideServer> StartSideServerAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate, Func<HttpContext, Task> respond)
    {
        var received = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (certificate is not null)
            {
                listen.UseHttps(certificate);
            }
        }));
        if (certificate is not null)
        {
            builder.WebHost.UseKestrelHttpsConfiguration();
        }

        var app = builder.Build();
        app.Run(context =>
        {
            received.Enqueue(context.Request.Path);
            return respond(context);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new SideServer(app, new Uri(address).Port, received);
    }

    private sealed record SideServer(WebApplication App, int Port, ConcurrentQueue<string> Received) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}

internal sealed class MemoryPinStore : IServerPinStore
{
    public string? Saved { get; private set; }

    public string? Load() => Saved;

    public void Save(string fingerprint) => Saved = fingerprint;

    public void Clear() => Saved = null;
}

internal sealed class MemoryTokenStore : IPosteTokenStore
{
    public string? Saved { get; private set; }

    public string? Load() => Saved;

    public void Save(string token) => Saved = token;

    public void Clear() => Saved = null;
}
