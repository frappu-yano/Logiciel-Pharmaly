using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Stock;

namespace Pharmaly.Tests.Server;

public sealed class StockTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private static readonly TimeSpan ConcurrencyTimeout = TimeSpan.FromSeconds(60);
    private readonly StockTestKit kit = new(server);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Après chaque scénario : somme des mouvements = solde, sur toute la base.</summary>
    public async ValueTask DisposeAsync() => await StockInvariant.AssertHoldsAsync(server.DataSource);

    private static Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> action) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(() => action(index)))).WaitAsync(ConcurrencyTimeout);

    // ---------- Concurrence ----------

    [Fact]
    public async Task Cinquante_sorties_concurrentes_sur_un_stock_de_trente_trente_reussissent_et_vingt_sont_refusees()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        await kit.SetStockAsync(product.Id, location, 30);

        var responses = await InParallelAsync(50, _ => StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, -1));

        Assert.Equal(30, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        var refused = responses.Where(response => response.StatusCode != HttpStatusCode.OK).ToList();
        Assert.Equal(20, refused.Count);
        foreach (var response in refused)
        {
            var error = await StockTestKit.AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
            Assert.Contains(product.Name, error.Message);
        }

        Assert.Equal(0m, await kit.ReadLevelAsync(product.Id, location));
        // 1 mouvement d'inventaire initial + 30 sorties, pas une de plus.
        Assert.Equal(31, await kit.CountMovementsAsync(product.Id));
        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(0m, await connection.ExecuteScalarAsync<decimal>(
            "SELECT min(balance_after) FROM stock_movements WHERE product_id = @Id", new { product.Id }));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Deux_demandes_simultanees_sur_le_dernier_exemplaire_une_seule_reussit()
    {
        var location = await kit.MainLocationAsync();
        var (first, _) = await kit.CreateEmployeeClientAsync(Permissions.StockAdjust);
        var (second, _) = await kit.CreateEmployeeClientAsync(Permissions.StockAdjust);

        // Plusieurs manches : la course doit être gagnée par un seul à chaque fois.
        for (var round = 0; round < 10; round++)
        {
            var product = await kit.CreateProductAsync();
            await kit.SetStockAsync(product.Id, location, 1);

            var responses = await InParallelAsync(
                2, index => StockTestKit.AdjustByAsync(index == 0 ? first : second, product.Id, location, -1));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var error = await StockTestKit.AssertErrorAsync(
                responses.Single(response => response.StatusCode != HttpStatusCode.OK),
                HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
            Assert.Contains("1 demandé", error.Message);
            Assert.Contains("0 disponible", error.Message);
            Assert.Equal(0m, await kit.ReadLevelAsync(product.Id, location));
        }

        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Des_mouvements_croises_sur_plusieurs_produits_ne_provoquent_aucun_interblocage()
    {
        var main = await kit.MainLocationAsync();
        var aside = await kit.SetAsideLocationAsync();
        var a = await kit.CreateProductAsync();
        var b = await kit.CreateProductAsync();
        foreach (var product in new[] { a, b })
        {
            await kit.SetStockAsync(product.Id, main, 500);
            await kit.SetStockAsync(product.Id, aside, 500);
        }

        // A puis B dans un sens, B puis A dans l'autre, et des ajustements isolés au milieu.
        var responses = await InParallelAsync(60, index => (index % 3) switch
        {
            0 => StockTestKit.TransferAsync(server.AdminClient, main, aside, null, (a.Id, 1), (b.Id, 2)),
            1 => StockTestKit.TransferAsync(server.AdminClient, aside, main, null, (b.Id, 2), (a.Id, 1)),
            _ => StockTestKit.AdjustByAsync(server.AdminClient, index % 2 == 0 ? a.Id : b.Id, index % 4 < 2 ? main : aside, 1),
        });

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        // Les transferts se compensent ; restent les 20 ajustements de +1.
        var total = await kit.ReadLevelAsync(a.Id, main) + await kit.ReadLevelAsync(a.Id, aside)
                    + await kit.ReadLevelAsync(b.Id, main) + await kit.ReadLevelAsync(b.Id, aside);
        Assert.Equal(2020m, total);
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Le_moteur_applique_sans_interblocage_des_listes_de_mouvements_en_ordre_inverse()
    {
        var engine = server.Services.GetRequiredService<StockEngine>();
        var main = await kit.MainLocationAsync();
        var a = await kit.CreateProductAsync();
        var b = await kit.CreateProductAsync();
        var c = await kit.CreateProductAsync();

        // Aucun solde n'existe encore : la création des lignes est elle aussi en concurrence.
        await InParallelAsync(30, async index =>
        {
            var products = index % 2 == 0 ? new[] { a.Id, b.Id, c.Id } : [c.Id, b.Id, a.Id];
            await ApplyDirectlyAsync(engine, products
                .Select(productId => StockChange.Move(productId, main, 2, StockMovementTypes.Adjustment)).ToList());
            return true;
        });

        foreach (var product in new[] { a, b, c })
        {
            Assert.Equal(60m, await kit.ReadLevelAsync(product.Id, main));
        }

        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    // ---------- Idempotence ----------

    [Fact]
    public async Task Le_meme_identifiant_d_operation_rejoue_ne_produit_qu_un_effet_et_la_meme_reponse()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        await kit.SetStockAsync(product.Id, location, 10);
        var operationId = Guid.NewGuid();

        var first = await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, -3, operationId);
        var replay = await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, -3, operationId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = await first.Content.ReadFromJsonAsync<StockAdjustmentResult>();
        Assert.Equal(new StockAdjustmentResult(product.Id, location, 10, -3, 7), result);
        Assert.Equal(result, await replay.Content.ReadFromJsonAsync<StockAdjustmentResult>());
        Assert.Equal(7m, await kit.ReadLevelAsync(product.Id, location));
        Assert.Equal(2, await kit.CountMovementsAsync(product.Id));
        // Un seul audit pour la sortie (l'autre est celui de l'inventaire initial).
        Assert.Equal(2, await kit.CountAuditAsync(AuditActions.StockAdjusted, product.Id.ToString()));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Le_meme_identifiant_d_operation_envoye_en_parallele_ne_produit_qu_un_effet_et_la_meme_reponse()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        await kit.SetStockAsync(product.Id, location, 10);
        var operationId = Guid.NewGuid();

        var responses = await InParallelAsync(
            12, _ => StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, -1, operationId));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var results = new List<StockAdjustmentResult>();
        foreach (var response in responses)
        {
            results.Add((await response.Content.ReadFromJsonAsync<StockAdjustmentResult>())!);
        }

        Assert.All(results, result => Assert.Equal(new StockAdjustmentResult(product.Id, location, 10, -1, 9), result));
        Assert.Equal(9m, await kit.ReadLevelAsync(product.Id, location));
        Assert.Equal(2, await kit.CountMovementsAsync(product.Id));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Un_identifiant_d_operation_ne_peut_pas_servir_a_une_autre_demande()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        var operationId = Guid.NewGuid();
        (await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, 5, operationId)).EnsureSuccessStatusCode();

        var other = await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, 50, operationId);

        await StockTestKit.AssertErrorAsync(other, HttpStatusCode.Conflict, ApiErrorCodes.OperationMismatch);
        Assert.Equal(5m, await kit.ReadLevelAsync(product.Id, location));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Une_ecriture_sans_identifiant_d_operation_est_refusee()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();

        var adjustment = await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, 5, Guid.Empty);
        var transfer = await StockTestKit.TransferAsync(
            server.AdminClient, location, await kit.SetAsideLocationAsync(), Guid.Empty, (product.Id, 1));

        await StockTestKit.AssertErrorAsync(adjustment, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(transfer, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        Assert.Equal(0, await kit.CountMovementsAsync(product.Id));
    }

    [Fact]
    public async Task Une_commande_refusee_n_enregistre_rien_et_peut_etre_retentee_avec_le_meme_identifiant()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        var operationId = Guid.NewGuid();

        var refused = await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, -1, operationId);
        await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
        await using (var connection = await server.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM operations WHERE operation_id = @operationId", new { operationId }));
        }

        await kit.SetStockAsync(product.Id, location, 1);
        var retried = await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, location, -1, operationId);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(0m, await kit.ReadLevelAsync(product.Id, location));
    }

    // ---------- Règles du moteur ----------

    [Fact]
    public async Task Le_refus_pour_stock_insuffisant_nomme_le_produit_la_quantite_demandee_et_la_quantite_disponible()
    {
        var engine = server.Services.GetRequiredService<StockEngine>();
        var product = await kit.CreateProductAsync("Sérum d'essai 30 ml");
        var location = await kit.MainLocationAsync();
        await kit.SetStockAsync(product.Id, location, 2);

        var exception = await Assert.ThrowsAsync<InsufficientStockException>(() =>
            ApplyDirectlyAsync(engine, [StockChange.Move(product.Id, location, -5, StockMovementTypes.Sale)]));

        Assert.Equal("Sérum d'essai 30 ml", exception.ProductName);
        Assert.Equal(5m, exception.Requested);
        Assert.Equal(2m, exception.Available);
        Assert.Equal("Stock insuffisant pour « Sérum d'essai 30 ml » : 5 demandé, 2 disponible.", exception.Message);
        Assert.Equal(2m, await kit.ReadLevelAsync(product.Id, location));
    }

    [Fact]
    public async Task Une_quantite_fractionnaire_est_refusee_pour_un_produit_vendu_a_l_unite_et_acceptee_sinon()
    {
        var location = await kit.MainLocationAsync();
        var unit = await kit.CreateProductAsync(soldByUnit: true);
        var bulk = await kit.CreateProductAsync(soldByUnit: false);

        await StockTestKit.AssertErrorAsync(
            await StockTestKit.CountAsync(server.AdminClient, unit.Id, location, 1.5m),
            HttpStatusCode.BadRequest, ApiErrorCodes.FractionalQuantity);
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(server.AdminClient, unit.Id, location, 0.5m),
            HttpStatusCode.BadRequest, ApiErrorCodes.FractionalQuantity);
        Assert.Equal(0, await kit.CountMovementsAsync(unit.Id));

        (await StockTestKit.CountAsync(server.AdminClient, bulk.Id, location, 1.5m)).EnsureSuccessStatusCode();
        (await StockTestKit.AdjustByAsync(server.AdminClient, bulk.Id, location, -0.125m)).EnsureSuccessStatusCode();
        Assert.Equal(1.375m, await kit.ReadLevelAsync(bulk.Id, location));

        // Plus de trois décimales : refusé plutôt qu'arrondi en silence.
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(server.AdminClient, bulk.Id, location, 0.0001m),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task La_base_elle_meme_refuse_une_fraction_sur_un_produit_a_l_unite_et_un_solde_negatif()
    {
        var location = await kit.MainLocationAsync();
        var unit = await kit.CreateProductAsync(soldByUnit: true);
        var bulk = await kit.CreateProductAsync(soldByUnit: false);
        await kit.SetStockAsync(bulk.Id, location, 0.5m);
        await using var connection = await server.DataSource.OpenConnectionAsync();

        var fraction = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "INSERT INTO stock_levels (product_id, location_id, quantity) VALUES (@Id, @location, 0.5)", new { unit.Id, location }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, fraction.SqlState);

        var negative = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "INSERT INTO stock_levels (product_id, location_id, quantity) VALUES (@Id, @location, -1)", new { unit.Id, location }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);

        var becomesUnit = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "UPDATE products SET sold_by_unit = true WHERE id = @Id", new { bulk.Id }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, becomesUnit.SqlState);
    }

    [Fact]
    public async Task Le_journal_de_stock_ne_peut_etre_ni_modifie_ni_supprime_ni_vide()
    {
        var product = await kit.CreateProductAsync();
        await kit.SetStockAsync(product.Id, await kit.MainLocationAsync(), 4);
        await using var connection = await server.DataSource.OpenConnectionAsync();

        var statements = new[]
        {
            "UPDATE stock_movements SET quantity = 99",
            "UPDATE stock_movements SET balance_after = 99",
            "DELETE FROM stock_movements",
            "TRUNCATE stock_movements",
            "TRUNCATE products CASCADE",
            "UPDATE product_sale_prices SET new_price = 1",
            "DELETE FROM product_sale_prices",
            "TRUNCATE product_sale_prices",
        };
        foreach (var statement in statements)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(statement));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, exception.SqlState);
        }

        Assert.Equal(1, await kit.CountMovementsAsync(product.Id));
    }

    // ---------- Ajustement d'inventaire ----------

    [Fact]
    public async Task Un_ajustement_fixe_la_quantite_comptee_et_ecrit_un_mouvement_et_un_audit()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        await kit.SetStockAsync(product.Id, location, 12);

        var response = await server.AdminClient.PostAsJsonAsync(
            ApiRoutes.StockAdjustments,
            new StockAdjustmentRequest(Guid.NewGuid(), product.Id, location, 9, null, "Trois flacons cassés"));

        Assert.Equal(
            new StockAdjustmentResult(product.Id, location, 12, -3, 9),
            await response.Content.ReadFromJsonAsync<StockAdjustmentResult>());
        await using var connection = await server.DataSource.OpenConnectionAsync();
        var movement = await connection.QuerySingleAsync<(decimal Quantity, string Type, string Note, decimal BalanceAfter, Guid EmployeeId)>(
            """
            SELECT quantity, movement_type, note, balance_after, employee_id FROM stock_movements
            WHERE product_id = @Id ORDER BY id DESC LIMIT 1
            """,
            new { product.Id });
        Assert.Equal((-3m, StockMovementTypes.Adjustment, "Trois flacons cassés", 9m, server.AdminId), movement);
        Assert.Equal(2, await kit.CountAuditAsync(AuditActions.StockAdjusted, product.Id.ToString()));

        // Recompter la même quantité ne crée aucun mouvement.
        (await StockTestKit.CountAsync(server.AdminClient, product.Id, location, 9)).EnsureSuccessStatusCode();
        Assert.Equal(2, await kit.CountMovementsAsync(product.Id));
    }

    [Fact]
    public async Task Un_ajustement_exige_un_motif_et_une_seule_facon_d_indiquer_la_quantite()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();
        var requests = new[]
        {
            new StockAdjustmentRequest(Guid.NewGuid(), product.Id, location, 5, null, "   "),
            new StockAdjustmentRequest(Guid.NewGuid(), product.Id, location, 5, 2, "Motif"),
            new StockAdjustmentRequest(Guid.NewGuid(), product.Id, location, null, null, "Motif"),
            new StockAdjustmentRequest(Guid.NewGuid(), product.Id, location, null, 0, "Motif"),
            new StockAdjustmentRequest(Guid.NewGuid(), product.Id, location, -1, null, "Motif"),
        };

        foreach (var request in requests)
        {
            await StockTestKit.AssertErrorAsync(
                await server.AdminClient.PostAsJsonAsync(ApiRoutes.StockAdjustments, request),
                HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        Assert.Equal(0, await kit.CountMovementsAsync(product.Id));
    }

    [Fact]
    public async Task Un_produit_ou_un_emplacement_inconnu_est_signale_introuvable()
    {
        var product = await kit.CreateProductAsync();
        var location = await kit.MainLocationAsync();

        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(server.AdminClient, Guid.NewGuid(), location, 1),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(server.AdminClient, product.Id, Guid.NewGuid(), 1),
            HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    // ---------- Transfert ----------

    [Fact]
    public async Task Un_transfert_sort_et_entre_les_quantites_dans_la_meme_operation()
    {
        var main = await kit.MainLocationAsync();
        var other = await kit.OtherStoreLocationAsync();
        var a = await kit.CreateProductAsync();
        var b = await kit.CreateProductAsync();
        await kit.SetStockAsync(a.Id, main, 5);
        await kit.SetStockAsync(b.Id, main, 8);
        var operationId = Guid.NewGuid();

        var response = await StockTestKit.TransferAsync(server.AdminClient, main, other, operationId, (a.Id, 3), (b.Id, 8));

        var result = (await response.Content.ReadFromJsonAsync<StockTransferResult>())!;
        Assert.Equal(operationId, result.TransferId);
        Assert.Equal(
            new[] { new StockTransferResultLine(a.Id, 3, 2, 3), new StockTransferResultLine(b.Id, 8, 0, 8) },
            result.Lines);
        Assert.Equal(2m, await kit.ReadLevelAsync(a.Id, main));
        Assert.Equal(3m, await kit.ReadLevelAsync(a.Id, other));
        Assert.Equal(1, await kit.CountMovementsAsync(a.Id, StockMovementTypes.TransferOut));
        Assert.Equal(1, await kit.CountMovementsAsync(a.Id, StockMovementTypes.TransferIn));
        Assert.Equal(1, await kit.CountAuditAsync(AuditActions.StockTransferred, operationId.ToString()));

        // Rejoué : même réponse, aucun mouvement de plus.
        var replay = await StockTestKit.TransferAsync(server.AdminClient, main, other, operationId, (a.Id, 3), (b.Id, 8));
        Assert.Equal(result.Lines, (await replay.Content.ReadFromJsonAsync<StockTransferResult>())!.Lines);
        Assert.Equal(2m, await kit.ReadLevelAsync(a.Id, main));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Un_transfert_est_tout_ou_rien_si_une_ligne_manque_de_stock()
    {
        var main = await kit.MainLocationAsync();
        var aside = await kit.SetAsideLocationAsync();
        var a = await kit.CreateProductAsync();
        var b = await kit.CreateProductAsync("Baume d'essai");
        await kit.SetStockAsync(a.Id, main, 5);
        await kit.SetStockAsync(b.Id, main, 1);
        var operationId = Guid.NewGuid();

        var response = await StockTestKit.TransferAsync(server.AdminClient, main, aside, operationId, (a.Id, 3), (b.Id, 2));

        var error = await StockTestKit.AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.InsufficientStock);
        Assert.Equal("Stock insuffisant pour « Baume d'essai » : 2 demandé, 1 disponible.", error.Message);
        Assert.Equal(5m, await kit.ReadLevelAsync(a.Id, main));
        Assert.Equal(1m, await kit.ReadLevelAsync(b.Id, main));
        Assert.Equal(0m, await kit.ReadLevelAsync(a.Id, aside));
        Assert.Equal(0, await kit.CountMovementsAsync(a.Id, StockMovementTypes.TransferOut));
        Assert.Equal(0, await kit.CountMovementsAsync(a.Id, StockMovementTypes.TransferIn));
        Assert.Equal(0, await kit.CountAuditAsync(AuditActions.StockTransferred, operationId.ToString()));
        await StockInvariant.AssertHoldsAsync(server.DataSource);
    }

    [Fact]
    public async Task Un_transfert_mal_forme_est_refuse()
    {
        var main = await kit.MainLocationAsync();
        var aside = await kit.SetAsideLocationAsync();
        var product = await kit.CreateProductAsync();
        await kit.SetStockAsync(product.Id, main, 5);

        var malformed = new[]
        {
            await StockTestKit.TransferAsync(server.AdminClient, main, main, null, (product.Id, 1)),
            await StockTestKit.TransferAsync(server.AdminClient, main, aside),
            await StockTestKit.TransferAsync(server.AdminClient, main, aside, null, (product.Id, 0)),
            await StockTestKit.TransferAsync(server.AdminClient, main, aside, null, (product.Id, -2)),
            await StockTestKit.TransferAsync(server.AdminClient, main, aside, null, (product.Id, 1), (product.Id, 1)),
        };

        foreach (var response in malformed)
        {
            await StockTestKit.AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        Assert.Equal(5m, await kit.ReadLevelAsync(product.Id, main));
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task Sans_le_droit_l_ajustement_et_le_transfert_sont_refuses()
    {
        var main = await kit.MainLocationAsync();
        var aside = await kit.SetAsideLocationAsync();
        var product = await kit.CreateProductAsync();
        await kit.SetStockAsync(product.Id, main, 5);
        var (viewer, _) = await kit.CreateEmployeeClientAsync(Permissions.ProductsView);
        var (adjuster, _) = await kit.CreateEmployeeClientAsync(Permissions.StockAdjust);
        var (mover, _) = await kit.CreateEmployeeClientAsync(Permissions.StockTransfer);

        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(viewer, product.Id, main, 1), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(mover, product.Id, main, 1), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.TransferAsync(viewer, main, aside, null, (product.Id, 1)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.TransferAsync(adjuster, main, aside, null, (product.Id, 1)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(5m, await kit.ReadLevelAsync(product.Id, main));

        (await StockTestKit.AdjustByAsync(adjuster, product.Id, main, 1)).EnsureSuccessStatusCode();
        (await StockTestKit.TransferAsync(mover, main, aside, null, (product.Id, 2))).EnsureSuccessStatusCode();
        Assert.Equal(4m, await kit.ReadLevelAsync(product.Id, main));
    }

    [Fact]
    public async Task Un_droit_de_stock_limite_a_un_site_ne_vaut_pas_pour_les_emplacements_d_un_autre_site()
    {
        var main = await kit.MainLocationAsync();
        var other = await kit.OtherStoreLocationAsync();
        var product = await kit.CreateProductAsync();
        await kit.SetStockAsync(product.Id, main, 5);
        var (local, localId) = await kit.CreateScopedEmployeeClientAsync(
            new PermissionGrant(Permissions.StockAdjust, server.StoreId),
            new PermissionGrant(Permissions.StockTransfer, server.StoreId));

        await StockTestKit.AssertErrorAsync(
            await StockTestKit.AdjustByAsync(local, product.Id, other, 1), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await StockTestKit.TransferAsync(local, main, other, null, (product.Id, 1)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);

        Assert.Equal(0m, await kit.ReadLevelAsync(product.Id, other));
        await using var connection = await server.DataSource.OpenConnectionAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM audit_logs WHERE action = @action AND employee_id = @localId",
            new { action = AuditActions.PermissionDenied, localId }));

        (await StockTestKit.AdjustByAsync(local, product.Id, main, 1)).EnsureSuccessStatusCode();
        (await StockTestKit.TransferAsync(local, main, await kit.SetAsideLocationAsync(), null, (product.Id, 1))).EnsureSuccessStatusCode();
    }

    // ---------- Emplacements ----------

    [Fact]
    public async Task Chaque_site_a_un_emplacement_vendable_et_un_emplacement_des_produits_mis_a_l_ecart()
    {
        var (employee, _) = await kit.CreateEmployeeClientAsync(Permissions.ProductsView);

        var locations = (await employee.GetFromJsonAsync<List<StockLocationSummary>>(ApiRoutes.StockLocations))!;

        foreach (var storeId in new[] { server.StoreId, server.OtherStoreId })
        {
            var ofStore = locations.Where(location => location.StoreId == storeId).ToList();
            Assert.Equal(2, ofStore.Count);
            Assert.Single(ofStore, location =>
                location is { Code: SiteSetup.MainLocationCode, Kind: StockLocationKinds.Sellable });
            Assert.Single(ofStore, location =>
                location is { Code: SiteSetup.SetAsideLocationCode, Kind: StockLocationKinds.SetAside });
        }

        var poste = await server.CreatePosteClientAsync();
        await StockTestKit.AssertErrorAsync(
            await poste.GetAsync(ApiRoutes.StockLocations), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
    }

    /// <summary>Appelle le moteur comme le fera une vente : une transaction, une opération inscrite.</summary>
    private async Task ApplyDirectlyAsync(StockEngine engine, IReadOnlyList<StockChange> changes)
    {
        var operationId = Guid.NewGuid();
        await using var connection = await server.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO operations (operation_id, kind, employee_id, request_hash, result) VALUES (@operationId, 'ESSAI', @AdminId, '', '{}')",
            new { operationId, server.AdminId },
            transaction);
        await engine.ApplyAsync(connection, transaction, server.AdminId, operationId, changes);
        await transaction.CommitAsync();
    }
}
