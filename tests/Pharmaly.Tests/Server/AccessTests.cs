using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;

namespace Pharmaly.Tests.Server;

public sealed class AccessTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private const string SearchRoute = ApiRoutes.ProductSearch + "?q=creme";
    private static int nextEmployeeNumber = 100;

    private async Task<(Guid Id, int Number, string Secret)> CreateEmployeeAsync(params PermissionGrant[] grants)
    {
        var number = Interlocked.Increment(ref nextEmployeeNumber);
        const string Secret = "482615";
        var response = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Employees, new CreateEmployeeRequest(number, $"Employé {number}", Secret));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<EmployeeSummary>())!;
        if (grants.Length > 0)
        {
            (await SetPermissionsAsync(server.AdminClient, created.Id, grants)).EnsureSuccessStatusCode();
        }

        return (created.Id, number, Secret);
    }

    private static Task<HttpResponseMessage> SetPermissionsAsync(HttpClient client, Guid employeeId, params PermissionGrant[] grants) =>
        client.PutAsJsonAsync($"{ApiRoutes.Employees}/{employeeId}/permissions", new SetPermissionsRequest(grants));

    private async Task<HttpClient> SignedInClientAsync(int number, string secret)
    {
        var client = await server.CreatePosteClientAsync();
        await ServerFixture.IdentifyAsync(client, number, secret);
        return client;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(code, error!.Code);
    }

    private async Task<int> CountAuditAsync(string action, Guid? employeeId = null)
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM audit_logs WHERE action = @action AND (@employeeId::uuid IS NULL OR employee_id = @employeeId)",
            new { action, employeeId });
    }

    [Fact]
    public async Task Sans_compte_de_caisse_aucune_donnee_n_est_servie() =>
        await AssertErrorAsync(await server.Client.GetAsync(SearchRoute), HttpStatusCode.Unauthorized, ApiErrorCodes.PosteNotSignedIn);

    [Fact]
    public async Task Un_jeton_invente_est_refuse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SearchRoute);
        request.Headers.Add(ApiHeaders.PosteToken, "jeton-invente");
        request.Headers.Add(ApiHeaders.EmployeeToken, "jeton-invente");

        await AssertErrorAsync(await server.Client.SendAsync(request), HttpStatusCode.Unauthorized, ApiErrorCodes.PosteNotSignedIn);
    }

    [Fact]
    public async Task Un_poste_connecte_sans_employe_identifie_ne_peut_rien_consulter()
    {
        var poste = await server.CreatePosteClientAsync();

        await AssertErrorAsync(await poste.GetAsync(SearchRoute), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
    }

    [Fact]
    public async Task Un_mauvais_mot_de_passe_de_caisse_est_refuse_et_audite()
    {
        var before = await CountAuditAsync(AuditActions.PosteSignInRefused);

        var response = await server.Client.PostAsJsonAsync(
            ApiRoutes.PosteSignIn, new PosteSignInRequest(ServerFixture.RegisterLogin, "mauvais"));

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.InvalidCredentials);
        Assert.Equal(before + 1, await CountAuditAsync(AuditActions.PosteSignInRefused));
    }

    [Fact]
    public async Task Un_employe_sans_le_droit_est_refuse_par_le_serveur_et_le_refus_est_audite()
    {
        var employee = await CreateEmployeeAsync();
        var client = await SignedInClientAsync(employee.Number, employee.Secret);

        await AssertErrorAsync(await client.GetAsync(SearchRoute), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(1, await CountAuditAsync(AuditActions.PermissionDenied, employee.Id));
    }

    [Fact]
    public async Task Un_droit_accorde_ouvre_l_acces_et_son_retrait_le_referme()
    {
        var employee = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var client = await SignedInClientAsync(employee.Number, employee.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SearchRoute)).StatusCode);

        (await SetPermissionsAsync(server.AdminClient, employee.Id)).EnsureSuccessStatusCode();

        // Le retrait vaut immédiatement, sans attendre une nouvelle identification.
        await AssertErrorAsync(await client.GetAsync(SearchRoute), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
    }

    [Fact]
    public async Task Un_droit_limite_a_un_autre_magasin_ne_vaut_pas_ici()
    {
        var elsewhere = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, server.OtherStoreId));
        var here = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, server.StoreId));

        var elsewhereClient = await SignedInClientAsync(elsewhere.Number, elsewhere.Secret);
        var hereClient = await SignedInClientAsync(here.Number, here.Secret);

        await AssertErrorAsync(await elsewhereClient.GetAsync(SearchRoute), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(HttpStatusCode.OK, (await hereClient.GetAsync(SearchRoute)).StatusCode);
    }

    [Fact]
    public async Task Nul_ne_peut_modifier_ses_propres_droits_meme_administrateur()
    {
        var response = await SetPermissionsAsync(
            server.AdminClient, server.AdminId, new PermissionGrant(Permissions.UsersAdminister, null));

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.SelfModificationForbidden);
    }

    [Fact]
    public async Task Un_employe_sans_le_droit_d_administration_ne_peut_accorder_aucun_droit()
    {
        var employee = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var other = await CreateEmployeeAsync();
        var client = await SignedInClientAsync(employee.Number, employee.Secret);
        var everything = Permissions.All.Select(permission => new PermissionGrant(permission.Code, null)).ToArray();

        await AssertErrorAsync(await SetPermissionsAsync(client, employee.Id, everything), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await AssertErrorAsync(await SetPermissionsAsync(client, other.Id, everything), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await AssertErrorAsync(await client.GetAsync(ApiRoutes.Employees), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
    }

    [Fact]
    public async Task Un_droit_inconnu_est_refuse()
    {
        var employee = await CreateEmployeeAsync();

        var response = await SetPermissionsAsync(server.AdminClient, employee.Id, new PermissionGrant("droit.invente", null));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Chaque_changement_de_droits_est_trace_avec_son_auteur_et_son_detail()
    {
        var employee = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        (await SetPermissionsAsync(server.AdminClient, employee.Id, new PermissionGrant(Permissions.SalesCreate, null))).EnsureSuccessStatusCode();

        await using var connection = await server.DataSource.OpenConnectionAsync();
        var entries = (await connection.QueryAsync<(Guid Author, string Details)>(
            """
            SELECT employee_id, details::text FROM audit_logs
            WHERE action = @action AND entity_id = @id ORDER BY id
            """,
            new { action = AuditActions.PermissionsChanged, id = employee.Id.ToString() })).ToList();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(server.AdminId, entry.Author));
        Assert.Contains(Permissions.SalesCreate, entries[1].Details);
        Assert.Contains("retires", entries[1].Details);
        Assert.Contains(Permissions.ProductsView, entries[1].Details);
    }

    [Fact]
    public async Task Cinq_codes_faux_verrouillent_le_compte_meme_pour_le_bon_code()
    {
        var employee = await CreateEmployeeAsync();
        var poste = await server.CreatePosteClientAsync();

        for (var attempt = 0; attempt < AccessService.MaxFailedAttempts; attempt++)
        {
            var refused = await poste.PostAsJsonAsync(ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employee.Number, "000000"));
            await AssertErrorAsync(refused, HttpStatusCode.Unauthorized, ApiErrorCodes.InvalidCredentials);
        }

        var locked = await poste.PostAsJsonAsync(ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employee.Number, employee.Secret));
        await AssertErrorAsync(locked, HttpStatusCode.Locked, ApiErrorCodes.Locked);
        Assert.Equal(1, await CountAuditAsync(AuditActions.AccountLocked, employee.Id));
    }

    [Fact]
    public async Task Un_matricule_inconnu_recoit_la_meme_reponse_qu_un_code_faux()
    {
        var poste = await server.CreatePosteClientAsync();

        var response = await poste.PostAsJsonAsync(ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(999_999, "123456"));

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Desactiver_un_employe_coupe_sa_session_immediatement()
    {
        var employee = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var client = await SignedInClientAsync(employee.Number, employee.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SearchRoute)).StatusCode);

        (await server.AdminClient.PutAsJsonAsync($"{ApiRoutes.Employees}/{employee.Id}/etat", new SetEmployeeStateRequest(false)))
            .EnsureSuccessStatusCode();

        await AssertErrorAsync(await client.GetAsync(SearchRoute), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
        var poste = await server.CreatePosteClientAsync();
        var signIn = await poste.PostAsJsonAsync(ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employee.Number, employee.Secret));
        await AssertErrorAsync(signIn, HttpStatusCode.Unauthorized, ApiErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Un_administrateur_ne_peut_pas_se_desactiver_lui_meme()
    {
        var response = await server.AdminClient.PutAsJsonAsync(
            $"{ApiRoutes.Employees}/{server.AdminId}/etat", new SetEmployeeStateRequest(false));

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.SelfModificationForbidden);
    }

    [Fact]
    public async Task Changer_d_employe_sur_un_poste_ferme_la_session_precedente()
    {
        var first = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var second = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var poste = await server.CreatePosteClientAsync();
        var firstSession = await ServerFixture.IdentifyAsync(poste, first.Number, first.Secret);

        await ServerFixture.IdentifyAsync(poste, second.Number, second.Secret);

        using var request = new HttpRequestMessage(HttpMethod.Get, SearchRoute);
        request.Headers.Add(ApiHeaders.PosteToken, poste.DefaultRequestHeaders.GetValues(ApiHeaders.PosteToken).Single());
        request.Headers.Add(ApiHeaders.EmployeeToken, firstSession.Token);
        await AssertErrorAsync(await server.Client.SendAsync(request), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);

        var identity = await poste.GetFromJsonAsync<CurrentIdentityResponse>(ApiRoutes.CurrentIdentity);
        Assert.Equal(second.Id, identity!.EmployeeId);
    }

    [Fact]
    public async Task Se_deconnecter_ferme_la_session_de_l_employe()
    {
        var employee = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var client = await SignedInClientAsync(employee.Number, employee.Secret);

        (await client.PostAsync(ApiRoutes.EmployeeSignOut, null)).EnsureSuccessStatusCode();

        await AssertErrorAsync(await client.GetAsync(SearchRoute), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
    }

    [Fact]
    public async Task Reinitialiser_un_code_invalide_l_ancien_et_ferme_les_sessions()
    {
        var employee = await CreateEmployeeAsync(new PermissionGrant(Permissions.ProductsView, null));
        var client = await SignedInClientAsync(employee.Number, employee.Secret);

        (await server.AdminClient.PutAsJsonAsync($"{ApiRoutes.Employees}/{employee.Id}/code", new ResetSecretRequest("905317")))
            .EnsureSuccessStatusCode();

        await AssertErrorAsync(await client.GetAsync(SearchRoute), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
        var poste = await server.CreatePosteClientAsync();
        var oldCode = await poste.PostAsJsonAsync(ApiRoutes.EmployeeSignIn, new EmployeeSignInRequest(employee.Number, employee.Secret));
        await AssertErrorAsync(oldCode, HttpStatusCode.Unauthorized, ApiErrorCodes.InvalidCredentials);
        await ServerFixture.IdentifyAsync(poste, employee.Number, "905317");
    }

    [Fact]
    public async Task Un_matricule_deja_attribue_et_un_code_trop_court_sont_refuses()
    {
        var duplicate = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.Employees, new CreateEmployeeRequest(ServerFixture.AdminNumber, "Doublon", "123456"));
        await AssertErrorAsync(duplicate, HttpStatusCode.Conflict, ApiErrorCodes.Conflict);

        var weak = await server.AdminClient.PostAsJsonAsync(ApiRoutes.Employees, new CreateEmployeeRequest(77_777, "Code court", "12"));
        await AssertErrorAsync(weak, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Aucun_secret_ni_jeton_n_est_stocke_en_clair()
    {
        var employee = await CreateEmployeeAsync();
        var poste = await server.CreatePosteClientAsync();
        var session = await ServerFixture.IdentifyAsync(poste, employee.Number, employee.Secret);
        var posteToken = poste.DefaultRequestHeaders.GetValues(ApiHeaders.PosteToken).Single();

        await using var connection = await server.DataSource.OpenConnectionAsync();
        var stored = string.Join('|', await connection.QueryAsync<string>(
            """
            SELECT secret_hash FROM employees
            UNION ALL SELECT password_hash FROM register_accounts
            UNION ALL SELECT token_hash FROM poste_sessions
            UNION ALL SELECT token_hash FROM employee_sessions
            UNION ALL SELECT details::text FROM audit_logs
            """));

        Assert.DoesNotContain(server.RegisterPassword, stored);
        Assert.DoesNotContain(session.Token, stored);
        Assert.DoesNotContain(posteToken, stored);
        Assert.DoesNotContain($"\"{employee.Secret}\"", stored);
    }

    [Fact]
    public async Task Le_journal_d_audit_ne_peut_etre_ni_modifie_ni_supprime_ni_vide()
    {
        await using var connection = await server.DataSource.OpenConnectionAsync();

        foreach (var statement in new[] { "UPDATE audit_logs SET action = 'X'", "DELETE FROM audit_logs", "TRUNCATE audit_logs" })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(statement));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, exception.SqlState);
        }

        Assert.True(await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM audit_logs") > 0);
    }
}
