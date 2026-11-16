using System.Net;
using System.Text;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Le transport est la seule pièce qui parle HTTP : jetons, en-têtes et lecture des refus.</summary>
public sealed class ServerTransportTests
{
    private readonly RecordingHandler handler = new();
    private readonly MemoryTokenStore tokenStore = new();

    private ServerTransport CreateTransport() => new(handler, new Uri("http://serveur.test:5080/"), tokenStore);

    [Fact]
    public async Task Les_jetons_du_poste_et_de_l_employe_accompagnent_chaque_demande()
    {
        using var transport = CreateTransport();
        var access = new AccessClient(transport);
        handler.Respond(HttpStatusCode.OK, """{"token":"jeton-poste","registerName":"Caisse 1","storeName":"Site"}""");
        handler.Respond(HttpStatusCode.OK, """{"token":"jeton-employe","employeeId":"6f0a3c0e-6f0e-4f43-9f6e-0d3b8c1d2a11","employeeName":"Samia","permissions":["admin.utilisateurs"]}""");
        handler.Respond(HttpStatusCode.OK, "[]");

        await access.SignInPosteAsync("caisse1", "secret", CancellationToken.None);
        var employee = await access.SignInEmployeeAsync(12, "482615", CancellationToken.None);
        await new AdminClient(transport).ListEmployeesAsync(CancellationToken.None);

        Assert.Equal(["admin.utilisateurs"], employee.Permissions);
        Assert.Equal("jeton-poste", tokenStore.Saved);
        Assert.True(access.HasPosteSession);
        Assert.Equal([null, "jeton-poste", "jeton-poste"], handler.Requests.Select(request => request.PosteToken));
        Assert.Equal([null, null, "jeton-employe"], handler.Requests.Select(request => request.EmployeeToken));
        Assert.Equal("http://serveur.test:5080/employes", handler.Requests[2].Url);
    }

    [Fact]
    public void Le_jeton_du_poste_memorise_est_repris_au_lancement()
    {
        tokenStore.Save("jeton-ancien");

        using var transport = CreateTransport();

        Assert.True(transport.HasPosteToken);
    }

    [Fact]
    public async Task Verrouiller_retire_le_jeton_de_l_employe_meme_si_le_serveur_refuse()
    {
        using var transport = CreateTransport();
        var access = new AccessClient(transport);
        handler.Respond(HttpStatusCode.OK, """{"token":"jeton-employe","employeeId":"6f0a3c0e-6f0e-4f43-9f6e-0d3b8c1d2a11","employeeName":"Samia","permissions":[]}""");
        handler.Respond(HttpStatusCode.Unauthorized, """{"code":"EMPLOYE_NON_IDENTIFIE","message":"Identifiez-vous."}""");
        handler.Respond(HttpStatusCode.OK, "[]");
        await access.SignInEmployeeAsync(12, "482615", CancellationToken.None);

        await Assert.ThrowsAsync<ServerRefusedException>(() => access.SignOutEmployeeAsync(CancellationToken.None));
        await new CatalogClient(transport).SearchProductsAsync("crème", CancellationToken.None);

        Assert.Equal("jeton-employe", handler.Requests[1].EmployeeToken);
        Assert.Null(handler.Requests[2].EmployeeToken);
        Assert.Equal("http://serveur.test:5080/produits/recherche?q=cr%C3%A8me", handler.Requests[2].Url);
    }

    [Fact]
    public async Task Un_refus_du_serveur_devient_une_exception_avec_son_code_et_son_message()
    {
        using var transport = CreateTransport();
        handler.Respond(HttpStatusCode.Forbidden, """{"code":"MODIFICATION_DE_SOI_INTERDITE","message":"Vous ne pouvez pas modifier vos propres droits."}""");

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() =>
            new AdminClient(transport).SetPermissionsAsync(Guid.NewGuid(), [], CancellationToken.None));

        Assert.Equal(ApiErrorCodes.SelfModificationForbidden, refused.Code);
        Assert.Equal("Vous ne pouvez pas modifier vos propres droits.", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Erreur</html>")]
    [InlineData("{}")]
    public async Task Une_erreur_sans_message_lisible_reste_un_refus_comprehensible(string body)
    {
        using var transport = CreateTransport();
        handler.Respond(HttpStatusCode.InternalServerError, body);

        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() =>
            new AdminClient(transport).ListEmployeesAsync(CancellationToken.None));

        Assert.Equal("Le serveur a répondu 500.", refused.Message);
    }

    [Fact]
    public async Task L_administration_envoie_les_droits_sur_la_route_de_l_employe()
    {
        using var transport = CreateTransport();
        var employeeId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        handler.Respond(HttpStatusCode.NoContent, string.Empty);
        handler.Respond(HttpStatusCode.NoContent, string.Empty);
        handler.Respond(HttpStatusCode.NoContent, string.Empty);
        var admin = new AdminClient(transport);

        await admin.SetPermissionsAsync(employeeId, [new PermissionGrant("ventes.vendre", storeId)], CancellationToken.None);
        await admin.SetEmployeeStateAsync(employeeId, false, CancellationToken.None);
        await admin.ResetSecretAsync(employeeId, "7410", CancellationToken.None);

        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Put, request.Method));
        Assert.Equal($"http://serveur.test:5080/employes/{employeeId}/permissions", handler.Requests[0].Url);
        Assert.Equal($$"""{"permissions":[{"code":"ventes.vendre","storeId":"{{storeId}}"}]}""", handler.Requests[0].Body);
        Assert.Equal($"http://serveur.test:5080/employes/{employeeId}/etat", handler.Requests[1].Url);
        Assert.Equal("""{"isActive":false}""", handler.Requests[1].Body);
        Assert.Equal($"http://serveur.test:5080/employes/{employeeId}/code", handler.Requests[2].Url);
        Assert.Equal("""{"secret":"7410"}""", handler.Requests[2].Body);
    }

    [Fact]
    public async Task Un_code_barres_inconnu_n_est_pas_une_erreur()
    {
        using var transport = CreateTransport();
        handler.Respond(HttpStatusCode.NotFound, string.Empty);

        Assert.Null(await new CatalogClient(transport).FindByBarcodeAsync("0000000000000", CancellationToken.None));
    }

    [Fact]
    public async Task L_etat_du_serveur_est_lu_meme_quand_la_base_est_indisponible()
    {
        using var transport = CreateTransport();
        handler.Respond(
            HttpStatusCode.ServiceUnavailable,
            """{"status":"BASE_INDISPONIBLE","version":"0.1.0","databaseReachable":false,"schemaVersion":0,"serverTime":"2026-11-16T08:00:00+01:00"}""");

        var health = await new AccessClient(transport).GetHealthAsync(CancellationToken.None);

        Assert.False(health!.DatabaseReachable);
    }

    private sealed record SentRequest(HttpMethod Method, string Url, string? PosteToken, string? EmployeeToken, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();

        public List<SentRequest> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body) => responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage message, string name) =>
                message.Headers.TryGetValues(name, out var values) ? values.Single() : null;

            Requests.Add(new SentRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                Header(request, ApiHeaders.PosteToken),
                Header(request, ApiHeaders.EmployeeToken),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            var (status, body) = responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class MemoryTokenStore : IPosteTokenStore
    {
        public string? Saved { get; private set; }

        public string? Load() => Saved;

        public void Save(string token) => Saved = token;

        public void Clear() => Saved = null;
    }
}
