using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.SupplierNotes;
using Kit = Pharmaly.Tests.Server.SupplierNoteTestKit;

namespace Pharmaly.Tests.Server;

public sealed class SupplierNoteTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private static readonly TimeSpan ConcurrencyTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] AllNoteRights =
        [Permissions.NotesView, Permissions.NotesEnter, Permissions.NotesEdit, Permissions.NotesValidate, Permissions.NotesCancel];

    private readonly StockTestKit stock = new(server);
    private readonly Kit notes = new(server);

    private HttpClient Admin => server.AdminClient;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Après chaque scénario : somme des mouvements = solde, sur toute la base.</summary>
    public async ValueTask DisposeAsync() => await StockInvariant.AssertHoldsAsync(server.DataSource);

    private static Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> action) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(() => action(index)))).WaitAsync(ConcurrencyTimeout);

    private static string[] AllNoteRightsExcept(params string[] excluded) => AllNoteRights.Except(excluded).ToArray();

    // ---------- Entrée immédiate en stock ----------

    [Fact]
    public async Task Une_ligne_de_15_sur_un_stock_de_10_donne_25_immediatement_et_le_bon_reste_en_saisie()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        await stock.SetStockAsync(product.Id, location, 10);
        var note = await notes.CreateNoteAsync(Admin, location);

        var result = await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15, 800m);

        Assert.Equal(10m, result.StockBefore);
        Assert.Equal(15m, result.StockChange);
        Assert.Equal(25m, result.StockAfter);
        Assert.Equal(25m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(product.Id, StockMovementTypes.NoteEntry));
        Assert.Equal(1, result.Line.Version);
        Assert.Equal(12000m, result.Line.LineTotal);
        Assert.Equal(1, result.NoteLineCount);
        Assert.Equal(12000m, result.NoteTotalAmount);

        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        Assert.Equal(SupplierNoteStatuses.InProgress, read.Note.Status);
        Assert.Equal(result.Line, Assert.Single(read.Lines));
        Assert.Equal([SupplierNoteEvents.Created, SupplierNoteEvents.LineAdded], read.History.Select(entry => entry.Event));
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LineAdded, note.Note.Id));

        // Le mouvement porte le bon comme origine et l'employé réel.
        await using var connection = await server.DataSource.OpenConnectionAsync();
        var movement = await connection.QuerySingleAsync<(string SourceType, string SourceId, Guid EmployeeId, decimal BalanceAfter)>(
            "SELECT source_type, source_id, employee_id, balance_after FROM stock_movements WHERE product_id = @Id AND movement_type = 'ENTREE_BON'",
            new { product.Id });
        Assert.Equal(("BON", note.Note.Id.ToString(), server.AdminId, 25m), movement);
    }

    [Fact]
    public async Task Le_meme_identifiant_d_operation_rejoue_n_entre_qu_une_fois_en_stock()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var note = await notes.CreateNoteAsync(Admin, location);
        var operationId = Guid.NewGuid();

        var first = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 15, 800m, operationId));
        var replay = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 15, 800m, operationId));

        Assert.Equal(first, replay);
        Assert.Equal(15m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(product.Id));
        Assert.Single((await Kit.ReadNoteAsync(Admin, note.Note.Id)).Lines);
        Assert.Equal(1, await notes.CountPurchasesAsync(product.Id));
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LineAdded, note.Note.Id));

        // Le même identifiant ne peut pas servir à une autre ligne.
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 99, 800m, operationId),
            HttpStatusCode.Conflict, ApiErrorCodes.OperationMismatch);
        Assert.Equal(15m, await stock.ReadLevelAsync(product.Id, location));
    }

    [Fact]
    public async Task Le_meme_identifiant_d_operation_envoye_en_parallele_n_entre_qu_une_fois_en_stock()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var note = await notes.CreateNoteAsync(Admin, location);
        var operationId = Guid.NewGuid();

        var responses = await InParallelAsync(12, _ => Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 15, 800m, operationId));

        var results = new List<SupplierNoteLineResult>();
        foreach (var response in responses)
        {
            results.Add(await Kit.ReadAsync<SupplierNoteLineResult>(response));
        }

        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(15m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(product.Id));
        Assert.Single((await Kit.ReadNoteAsync(Admin, note.Note.Id)).Lines);
        Assert.Equal(1, await notes.CountPurchasesAsync(product.Id));
    }

    [Fact]
    public async Task Vingt_lignes_enregistrees_en_meme_temps_sur_le_meme_bon_sont_toutes_presentes_et_le_stock_est_exact()
    {
        var location = await stock.MainLocationAsync();
        var products = new List<ProductSummary>();
        for (var index = 0; index < 5; index++)
        {
            products.Add(await stock.CreateProductAsync());
            await stock.SetStockAsync(products[index].Id, location, 10);
        }

        var note = await notes.CreateNoteAsync(Admin, location);

        // 20 lignes différentes : 4 par produit, de quantités 1 à 20.
        var responses = await InParallelAsync(20, index => Kit.AddLineAsync(Admin, note.Note.Id, products[index % 5].Id, index + 1, 100m + index));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        Assert.Equal(20, read.Lines.Count);
        Assert.Equal(Enumerable.Range(1, 20), read.Lines.Select(line => line.Position));
        Assert.Equal(Enumerable.Range(1, 20).Select(quantity => (decimal)quantity), read.Lines.Select(line => line.Quantity).Order());
        Assert.Equal(20, read.Note.LineCount);
        for (var index = 0; index < 5; index++)
        {
            // Produit n° index : quantités index+1, index+6, index+11, index+16.
            Assert.Equal(10m + (4 * index) + 34, await stock.ReadLevelAsync(products[index].Id, location));
            Assert.Equal(4, await stock.CountMovementsAsync(products[index].Id, StockMovementTypes.NoteEntry));
        }

        Assert.Equal(20, await notes.CountNoteMovementsAsync(note.Note.Id));
    }

    // ---------- Modification : seule la différence ----------

    [Fact]
    public async Task Modifier_une_ligne_applique_uniquement_la_difference_et_une_version_perimee_est_refusee()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        await stock.SetStockAsync(product.Id, location, 10);
        var note = await notes.CreateNoteAsync(Admin, location);
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15)).Line;

        // 15 → 9 : le stock baisse de 6.
        var reduced = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 9));
        Assert.Equal((25m, -6m, 19m), (reduced.StockBefore, reduced.StockChange, reduced.StockAfter));
        Assert.Equal(2, reduced.Line.Version);
        Assert.Equal(19m, await stock.ReadLevelAsync(product.Id, location));

        // 9 → 20 : le stock monte de 11.
        var raised = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 2, 20));
        Assert.Equal((19m, 11m, 30m), (raised.StockBefore, raised.StockChange, raised.StockAfter));
        Assert.Equal(3, raised.Line.Version);
        Assert.Equal(20m, raised.Line.Quantity);
        Assert.Equal(30m, await stock.ReadLevelAsync(product.Id, location));

        // Une demande fondée sur une version périmée est refusée et ne change rien.
        var stale = await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 5), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StaleVersion);
        Assert.Contains("Rechargez", stale.Message);
        await StockTestKit.AssertErrorAsync(
            await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 2), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StaleVersion);
        Assert.Equal(30m, await stock.ReadLevelAsync(product.Id, location));

        // Journal : une entrée de 15, deux corrections de -6 et +11.
        await using var connection = await server.DataSource.OpenConnectionAsync();
        var movements = (await connection.QueryAsync<(string Type, decimal Quantity)>(
            "SELECT movement_type, quantity FROM stock_movements WHERE source_id = @source ORDER BY id",
            new { source = note.Note.Id.ToString() })).ToList();
        Assert.Equal(
            [(StockMovementTypes.NoteEntry, 15m), (StockMovementTypes.NoteCorrection, -6m), (StockMovementTypes.NoteCorrection, 11m)],
            movements);
        Assert.Equal(2, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LineUpdated, note.Note.Id));
        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        Assert.Equal(2, read.History.Count(entry => entry.Event == SupplierNoteEvents.LineUpdated && entry.LineId == line.Id));
    }

    [Fact]
    public async Task Rejouer_une_modification_ne_l_applique_qu_une_fois_et_une_modification_sans_changement_ne_cree_rien()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var note = await notes.CreateNoteAsync(Admin, location);
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15)).Line;
        var operationId = Guid.NewGuid();

        var first = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 9, 800m, operationId));
        var replay = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 9, 800m, operationId));
        Assert.Equal(first, replay);
        Assert.Equal(9m, await stock.ReadLevelAsync(product.Id, location));

        // Mêmes valeurs qu'à l'écran : ni mouvement, ni historique, ni nouvelle version.
        var unchanged = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 2, 9));
        Assert.Equal(2, unchanged.Line.Version);
        Assert.Equal(0m, unchanged.StockChange);
        Assert.Equal(2, await stock.CountMovementsAsync(product.Id));
        Assert.Equal(2, await notes.CountPurchasesAsync(product.Id));
    }

    [Fact]
    public async Task Deux_modifications_simultanees_fondees_sur_la_meme_version_une_seule_est_appliquee()
    {
        var location = await stock.MainLocationAsync();
        var note = await notes.CreateNoteAsync(Admin, location);

        for (var round = 0; round < 8; round++)
        {
            var product = await stock.CreateProductAsync();
            var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15)).Line;

            var responses = await InParallelAsync(
                2, index => Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, index == 0 ? 9 : 20));

            var applied = await Kit.ReadAsync<SupplierNoteLineResult>(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK));
            await StockTestKit.AssertErrorAsync(
                responses.Single(response => response.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StaleVersion);
            Assert.Equal(2, applied.Line.Version);
            Assert.Equal(applied.Line.Quantity, await stock.ReadLevelAsync(product.Id, location));
            Assert.Equal(2, await stock.CountMovementsAsync(product.Id));
        }
    }

    // ---------- Annulation d'une ligne ----------

    [Fact]
    public async Task Annuler_une_ligne_cree_un_mouvement_compensatoire_audite()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        await stock.SetStockAsync(product.Id, location, 10);
        var note = await notes.CreateNoteAsync(Admin, location);
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15)).Line;
        var operationId = Guid.NewGuid();

        var cancelled = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 1, operationId));
        var replay = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 1, operationId));

        Assert.Equal(cancelled, replay);
        Assert.True(cancelled.Line.IsCancelled);
        Assert.Equal((25m, -15m, 10m), (cancelled.StockBefore, cancelled.StockChange, cancelled.StockAfter));
        Assert.Equal(0, cancelled.NoteLineCount);
        Assert.Equal(0m, cancelled.NoteTotalAmount);
        Assert.Equal(10m, await stock.ReadLevelAsync(product.Id, location));

        await using var connection = await server.DataSource.OpenConnectionAsync();
        var compensation = await connection.QuerySingleAsync<(decimal Quantity, decimal BalanceAfter, Guid EmployeeId)>(
            "SELECT quantity, balance_after, employee_id FROM stock_movements WHERE product_id = @Id AND movement_type = 'ANNULATION_BON'",
            new { product.Id });
        Assert.Equal((-15m, 10m, server.AdminId), compensation);
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LineCancelled, note.Note.Id));
        var details = await notes.ScalarAsync<string>(
            "SELECT details::text FROM audit_logs WHERE action = @action AND entity_id = @id",
            new { action = SupplierNoteAuditActions.LineCancelled, id = note.Note.Id.ToString() });
        Assert.Contains(line.Id.ToString(), details);

        // La ligne reste visible, annulée, et ne peut plus être modifiée ni annulée de nouveau.
        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        Assert.True(Assert.Single(read.Lines).IsCancelled);
        Assert.Contains(read.History, entry => entry.Event == SupplierNoteEvents.LineCancelled && entry.LineId == line.Id);
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 2, 3), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LineCancelled);
        await StockTestKit.AssertErrorAsync(
            await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 2), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LineCancelled);
        Assert.Equal(10m, await stock.ReadLevelAsync(product.Id, location));
    }

    // ---------- Stock déjà parti (décision D4) ----------

    [Fact]
    public async Task Reduire_ou_annuler_une_ligne_est_refuse_quand_le_stock_est_deja_parti_et_rien_ne_change()
    {
        var location = await stock.MainLocationAsync();
        var aside = await stock.SetAsideLocationAsync();
        var product = await stock.CreateProductAsync("Crème d'essai D4");
        var note = await notes.CreateNoteAsync(Admin, location);
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15)).Line;

        // 13 exemplaires quittent l'emplacement : 10 par transfert, 3 par ajustement. Il en reste 2.
        (await StockTestKit.TransferAsync(Admin, location, aside, null, (product.Id, 10))).EnsureSuccessStatusCode();
        (await StockTestKit.AdjustByAsync(Admin, product.Id, location, -3)).EnsureSuccessStatusCode();
        var movementsBefore = await stock.CountMovementsAsync(product.Id);

        var reduction = await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 9), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StockAlreadyGone);
        Assert.Contains("Crème d'essai D4", reduction.Message);
        Assert.Contains("de 15 à 9", reduction.Message);
        Assert.Contains("il n'en reste que 2 en stock", reduction.Message);
        Assert.Contains("Quantité minimale possible pour cette ligne : 13", reduction.Message);

        var cancellation = await StockTestKit.AssertErrorAsync(
            await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 1), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StockAlreadyGone);
        Assert.Contains("il n'en reste que 2 en stock", cancellation.Message);

        Assert.Equal(2m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(movementsBefore, await stock.CountMovementsAsync(product.Id));
        var unchanged = Assert.Single((await Kit.ReadNoteAsync(Admin, note.Note.Id)).Lines);
        Assert.Equal((15m, 1, false), (unchanged.Quantity, unchanged.Version, unchanged.IsCancelled));
        Assert.Equal(1, await notes.CountPurchasesAsync(product.Id));

        // Une réduction qui reste dans la quantité encore présente est acceptée : 15 → 13.
        var allowed = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 13));
        Assert.Equal(0m, allowed.StockAfter);
    }

    // ---------- Validation ----------

    [Fact]
    public async Task La_validation_ne_cree_aucun_mouvement_de_stock_et_un_second_appel_est_sans_effet()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        await stock.SetStockAsync(product.Id, location, 10);
        var note = await notes.CreateNoteAsync(Admin, location);
        await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15);
        var movementsBefore = await notes.ScalarAsync<int>("SELECT count(*)::int FROM stock_movements");
        var operationId = Guid.NewGuid();

        var validated = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.ValidateAsync(Admin, note.Note.Id, operationId));

        Assert.Equal(SupplierNoteStatuses.Validated, validated.Status);
        Assert.Null(validated.Lock);
        Assert.Equal(25m, await stock.ReadLevelAsync(product.Id, location));

        // Même commande rejouée, puis nouvelle commande : aucun effet.
        var replay = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.ValidateAsync(Admin, note.Note.Id, operationId));
        var second = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.ValidateAsync(Admin, note.Note.Id));
        Assert.Equal(validated, replay);
        Assert.Equal(SupplierNoteStatuses.Validated, second.Status);

        Assert.Equal(movementsBefore, await notes.ScalarAsync<int>("SELECT count(*)::int FROM stock_movements"));
        Assert.Equal(25m, await stock.ReadLevelAsync(product.Id, location));
        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        var entry = Assert.Single(read.History, entry => entry.Event == SupplierNoteEvents.Validated);
        Assert.Equal((SupplierNoteStatuses.InProgress, SupplierNoteStatuses.Validated), (entry.StatusBefore, entry.StatusAfter));
        Assert.Equal("Administratrice Test", entry.EmployeeName);
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.NoteValidated, note.Note.Id));
    }

    [Fact]
    public async Task Un_bon_sans_ligne_ne_se_valide_pas()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var note = await notes.CreateNoteAsync(Admin, location);

        await StockTestKit.AssertErrorAsync(
            await Kit.ValidateAsync(Admin, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.EmptyNote);

        // Une ligne annulée ne compte pas.
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 2)).Line;
        (await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 1)).EnsureSuccessStatusCode();
        await StockTestKit.AssertErrorAsync(
            await Kit.ValidateAsync(Admin, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.EmptyNote);
        Assert.Equal(SupplierNoteStatuses.InProgress, (await Kit.ReadNoteAsync(Admin, note.Note.Id)).Note.Status);
    }

    [Fact]
    public async Task Apres_validation_la_saisie_ordinaire_est_refusee_et_la_correction_exige_le_droit_de_modifier()
    {
        var location = await stock.MainLocationAsync();
        var aside = await stock.SetAsideLocationAsync();
        var product = await stock.CreateProductAsync();
        var other = await stock.CreateProductAsync();
        var (clerk, _) = await stock.CreateEmployeeClientAsync(Permissions.NotesView, Permissions.NotesEnter, Permissions.NotesValidate);
        var (corrector, _) = await stock.CreateEmployeeClientAsync(Permissions.NotesView, Permissions.NotesEdit);
        var note = await notes.CreateNoteAsync(clerk, location);
        var line = (await Kit.AddLineOkAsync(clerk, note.Note.Id, product.Id, 15)).Line;
        (await Kit.ValidateAsync(clerk, note.Note.Id)).EnsureSuccessStatusCode();

        // Le droit de saisir ne suffit plus, même en tenant le bon.
        (await Kit.AcquireAsync(clerk, note.Note.Id)).EnsureSuccessStatusCode();
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(clerk, note.Note.Id, line.Id, 1, 9), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await Kit.CancelLineAsync(clerk, note.Note.Id, line.Id, 1), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(clerk, note.Note.Id, other.Id, 1), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(15m, await stock.ReadLevelAsync(product.Id, location));
        (await Kit.ReleaseAsync(clerk, note.Note.Id)).EnsureSuccessStatusCode();

        // Avec « bons.modifier » : il faut tenir le bon, puis la correction applique la différence et est historisée.
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(corrector, note.Note.Id, line.Id, 1, 9), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockNotHeld);
        (await Kit.AcquireAsync(corrector, note.Note.Id)).EnsureSuccessStatusCode();
        var corrected = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.UpdateLineAsync(corrector, note.Note.Id, line.Id, 1, 9));
        Assert.Equal(-6m, corrected.StockChange);
        Assert.Equal(9m, await stock.ReadLevelAsync(product.Id, location));
        var added = await Kit.AddLineOkAsync(corrector, note.Note.Id, other.Id, 4);
        Assert.Equal(4m, await stock.ReadLevelAsync(other.Id, location));

        // Mêmes règles de stock : la marchandise partie ne se retire pas.
        (await StockTestKit.TransferAsync(Admin, location, aside, null, (product.Id, 8))).EnsureSuccessStatusCode();
        await StockTestKit.AssertErrorAsync(
            await Kit.CancelLineAsync(corrector, note.Note.Id, line.Id, 2), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StockAlreadyGone);
        (await Kit.CancelLineAsync(corrector, note.Note.Id, added.Line.Id, 1)).EnsureSuccessStatusCode();
        Assert.Equal(1m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(0m, await stock.ReadLevelAsync(other.Id, location));

        var read = await Kit.ReadNoteAsync(corrector, note.Note.Id);
        Assert.Equal(SupplierNoteStatuses.Validated, read.Note.Status);
        var corrections = read.History.Where(entry => entry.StatusBefore == SupplierNoteStatuses.Validated).Select(entry => entry.Event).ToList();
        Assert.Equal([SupplierNoteEvents.LineUpdated, SupplierNoteEvents.LineAdded, SupplierNoteEvents.LineCancelled], corrections);
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LineUpdated, note.Note.Id));
    }

    // ---------- Annulation d'un bon ----------

    [Fact]
    public async Task Annuler_un_bon_compense_toutes_ses_lignes_ou_aucune()
    {
        var location = await stock.MainLocationAsync();
        var aside = await stock.SetAsideLocationAsync();
        var a = await stock.CreateProductAsync("Produit A du bon annulé");
        var b = await stock.CreateProductAsync("Produit B du bon annulé");
        var c = await stock.CreateProductAsync("Produit C du bon annulé");
        await stock.SetStockAsync(a.Id, location, 5);
        var note = await notes.CreateNoteAsync(Admin, location);
        await Kit.AddLineOkAsync(Admin, note.Note.Id, a.Id, 10);
        await Kit.AddLineOkAsync(Admin, note.Note.Id, b.Id, 8);
        await Kit.AddLineOkAsync(Admin, note.Note.Id, b.Id, 4);
        var cancelledLine = (await Kit.AddLineOkAsync(Admin, note.Note.Id, c.Id, 6)).Line;
        (await Kit.CancelLineAsync(Admin, note.Note.Id, cancelledLine.Id, 1)).EnsureSuccessStatusCode();

        // Une partie de B a été transférée : il en reste 7 sur les 12 reçus.
        (await StockTestKit.TransferAsync(Admin, location, aside, null, (b.Id, 5))).EnsureSuccessStatusCode();
        var movementsBefore = await notes.CountNoteMovementsAsync(note.Note.Id);

        var refused = await StockTestKit.AssertErrorAsync(
            await Kit.CancelNoteAsync(Admin, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.StockAlreadyGone);
        Assert.Contains("« Produit B du bon annulé » (12 reçu, 7 encore en stock)", refused.Message);
        Assert.DoesNotContain("Produit A", refused.Message);

        // Rien n'a bougé, pas même le produit A qui aurait pu être compensé.
        Assert.Equal(15m, await stock.ReadLevelAsync(a.Id, location));
        Assert.Equal(7m, await stock.ReadLevelAsync(b.Id, location));
        Assert.Equal(movementsBefore, await notes.CountNoteMovementsAsync(note.Note.Id));
        Assert.Equal(SupplierNoteStatuses.InProgress, (await Kit.ReadNoteAsync(Admin, note.Note.Id)).Note.Status);
        Assert.Equal(0, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.NoteCancelled, note.Note.Id));

        // La marchandise revient : l'annulation compense toutes les lignes encore actives.
        (await StockTestKit.TransferAsync(Admin, aside, location, null, (b.Id, 5))).EnsureSuccessStatusCode();
        var operationId = Guid.NewGuid();
        var cancelled = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.CancelNoteAsync(Admin, note.Note.Id, operationId, "Livraison refusée"));
        var replay = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.CancelNoteAsync(Admin, note.Note.Id, operationId, "Livraison refusée"));
        var second = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.CancelNoteAsync(Admin, note.Note.Id));

        Assert.Equal(SupplierNoteStatuses.Cancelled, cancelled.Status);
        Assert.Equal(cancelled, replay);
        Assert.Equal(SupplierNoteStatuses.Cancelled, second.Status);
        Assert.Equal(5m, await stock.ReadLevelAsync(a.Id, location));
        Assert.Equal(0m, await stock.ReadLevelAsync(b.Id, location));
        Assert.Equal(0m, await stock.ReadLevelAsync(c.Id, location));
        // 3 compensations pour l'annulation du bon + 1 pour la ligne annulée auparavant.
        Assert.Equal(4, await notes.CountNoteMovementsAsync(note.Note.Id, StockMovementTypes.NoteCancellation));
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.NoteCancelled, note.Note.Id));
        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        var entry = Assert.Single(read.History, entry => entry.Event == SupplierNoteEvents.Cancelled);
        Assert.Equal((SupplierNoteStatuses.InProgress, SupplierNoteStatuses.Cancelled), (entry.StatusBefore, entry.StatusAfter));
        Assert.Contains("Livraison refusée", entry.Description);

        // Un bon annulé n'accepte plus rien.
        await StockTestKit.AssertErrorAsync(
            await Kit.AcquireAsync(Admin, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.NoteCancelled);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, a.Id, 1), HttpStatusCode.Conflict, SupplierNoteErrorCodes.NoteCancelled);
        await StockTestKit.AssertErrorAsync(
            await Kit.ValidateAsync(Admin, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.NoteCancelled);
        Assert.Equal(5m, await stock.ReadLevelAsync(a.Id, location));
    }

    [Fact]
    public async Task Un_bon_valide_s_annule_aussi_en_compensant_son_stock()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var note = await notes.CreateNoteAsync(Admin, location);
        await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15);
        (await Kit.ValidateAsync(Admin, note.Note.Id)).EnsureSuccessStatusCode();

        // La validation a libéré le bon : il faut le reprendre pour l'annuler.
        await StockTestKit.AssertErrorAsync(
            await Kit.CancelNoteAsync(Admin, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockNotHeld);
        (await Kit.AcquireAsync(Admin, note.Note.Id)).EnsureSuccessStatusCode();
        var cancelled = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.CancelNoteAsync(Admin, note.Note.Id));

        Assert.Equal(SupplierNoteStatuses.Cancelled, cancelled.Status);
        Assert.Equal(0m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(product.Id, StockMovementTypes.NoteCancellation));
    }

    // ---------- Verrou ----------

    [Fact]
    public async Task Un_second_employe_est_refuse_tant_que_le_premier_tient_le_bon_et_le_message_le_nomme()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var (first, firstId) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var (second, _) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var firstName = await notes.ScalarAsync<string>("SELECT full_name FROM employees WHERE id = @firstId", new { firstId });
        var note = await notes.CreateNoteAsync(first, location);
        var line = (await Kit.AddLineOkAsync(first, note.Note.Id, product.Id, 15)).Line;

        // Le bon créé est tenu par son créateur.
        Assert.Equal((firstId, true, false), (note.Note.Lock!.EmployeeId, note.Note.Lock.HeldByMe, note.Note.Lock.IsExpired));
        Assert.Equal("Caisse 1", note.Note.Lock.RegisterName);

        var refused = await StockTestKit.AssertErrorAsync(
            await Kit.AcquireAsync(second, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
        Assert.Contains(firstName, refused.Message);

        // Aucune écriture du second employé ne passe.
        var writes = new[]
        {
            await Kit.AddLineAsync(second, note.Note.Id, product.Id, 5),
            await Kit.UpdateLineAsync(second, note.Note.Id, line.Id, 1, 9),
            await Kit.CancelLineAsync(second, note.Note.Id, line.Id, 1),
            await Kit.ValidateAsync(second, note.Note.Id),
            await Kit.CancelNoteAsync(second, note.Note.Id),
            await Kit.RenewAsync(second, note.Note.Id),
            await Kit.ReleaseAsync(second, note.Note.Id),
        };
        foreach (var write in writes)
        {
            var error = await StockTestKit.AssertErrorAsync(write, HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
            Assert.Contains(firstName, error.Message);
        }

        Assert.Equal(15m, await stock.ReadLevelAsync(product.Id, location));
        var read = await Kit.ReadNoteAsync(second, note.Note.Id);
        Assert.Equal(SupplierNoteStatuses.InProgress, read.Note.Status);
        Assert.Equal((firstId, false), (read.Note.Lock!.EmployeeId, read.Note.Lock.HeldByMe));

        // Libération explicite : le second employé peut alors prendre le bon et écrire.
        Assert.Equal(HttpStatusCode.NoContent, (await Kit.ReleaseAsync(first, note.Note.Id)).StatusCode);
        var taken = await Kit.ReadAsync<SupplierNoteDetail>(await Kit.AcquireAsync(second, note.Note.Id));
        Assert.True(taken.Note.Lock!.HeldByMe);
        await Kit.AddLineOkAsync(second, note.Note.Id, product.Id, 5);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(first, note.Note.Id, product.Id, 1), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
        Assert.Equal(20m, await stock.ReadLevelAsync(product.Id, location));
    }

    [Fact]
    public async Task Deux_employes_qui_prennent_un_bon_libre_en_meme_temps_un_seul_l_obtient()
    {
        var location = await stock.MainLocationAsync();
        var (first, _) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var (second, _) = await stock.CreateEmployeeClientAsync(AllNoteRights);

        for (var round = 0; round < 8; round++)
        {
            var note = await notes.CreateNoteAsync(Admin, location);
            (await Kit.ReleaseAsync(Admin, note.Note.Id)).EnsureSuccessStatusCode();

            var responses = await InParallelAsync(2, index => Kit.AcquireAsync(index == 0 ? first : second, note.Note.Id));

            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            await StockTestKit.AssertErrorAsync(
                responses.Single(response => response.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
        }
    }

    [Fact]
    public async Task Une_ecriture_sans_tenir_le_bon_est_refusee()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var note = await notes.CreateNoteAsync(Admin, location);
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15)).Line;
        Assert.Equal(HttpStatusCode.NoContent, (await Kit.ReleaseAsync(Admin, note.Note.Id)).StatusCode);
        // Libérer un bon déjà libre est sans effet.
        Assert.Equal(HttpStatusCode.NoContent, (await Kit.ReleaseAsync(Admin, note.Note.Id)).StatusCode);

        var writes = new[]
        {
            await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 5),
            await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 9),
            await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 1),
            await Kit.ValidateAsync(Admin, note.Note.Id),
            await Kit.CancelNoteAsync(Admin, note.Note.Id),
            await Kit.RenewAsync(Admin, note.Note.Id),
        };
        foreach (var write in writes)
        {
            await StockTestKit.AssertErrorAsync(write, HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockNotHeld);
        }

        Assert.Equal(15m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(product.Id));
        var read = await Kit.ReadNoteAsync(Admin, note.Note.Id);
        Assert.Null(read.Note.Lock);
        Assert.Equal(SupplierNoteStatuses.InProgress, read.Note.Status);
    }

    [Fact]
    public async Task Le_meme_employe_reprend_son_bon_apres_deconnexion_meme_depuis_un_autre_poste()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var (firstPoste, employeeId) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var number = await notes.ScalarAsync<int>("SELECT employee_number FROM employees WHERE id = @employeeId", new { employeeId });
        var note = await notes.CreateNoteAsync(firstPoste, location);
        await Kit.AddLineOkAsync(firstPoste, note.Note.Id, product.Id, 15);

        // Déconnexion sans libérer le bon, puis identification sur un autre poste.
        (await firstPoste.PostAsync(ApiRoutes.EmployeeSignOut, null)).EnsureSuccessStatusCode();
        var secondPoste = await server.CreatePosteClientAsync();
        await ServerFixture.IdentifyAsync(secondPoste, number, Kit.EmployeeSecret);

        var resumed = await Kit.ReadAsync<SupplierNoteDetail>(await Kit.AcquireAsync(secondPoste, note.Note.Id));
        Assert.True(resumed.Note.Lock!.HeldByMe);
        Assert.Equal(note.Note.Lock!.AcquiredAt, resumed.Note.Lock.AcquiredAt);
        Assert.Single(resumed.Lines);
        await Kit.AddLineOkAsync(secondPoste, note.Note.Id, product.Id, 5);
        Assert.Equal(20m, await stock.ReadLevelAsync(product.Id, location));

        // Le battement prolonge le bail.
        var renewed = await Kit.ReadAsync<SupplierNoteLock>(await Kit.RenewAsync(secondPoste, note.Note.Id));
        Assert.True(renewed.ExpiresAt > resumed.Note.Lock.ExpiresAt);
        Assert.True(renewed.HeldByMe);
    }

    [Fact]
    public async Task A_l_expiration_du_bail_un_autre_employe_peut_prendre_le_bon_et_l_ancien_ne_peut_plus_ecrire()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var (first, firstId) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var (second, secondId) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var options = server.Services.GetRequiredService<SupplierNoteOptions>();
        Assert.Equal(SupplierNoteOptions.DefaultLockLease, options.LockLease);

        // Bail très court le temps de la création : pas d'attente réelle longue.
        SupplierNoteDetail note;
        options.LockLease = TimeSpan.FromMilliseconds(150);
        try
        {
            note = await notes.CreateNoteAsync(first, location);
        }
        finally
        {
            options.LockLease = SupplierNoteOptions.DefaultLockLease;
        }

        await Task.Delay(400);
        var expired = (await Kit.ReadNoteAsync(second, note.Note.Id)).Note.Lock!;
        Assert.Equal((firstId, true), (expired.EmployeeId, expired.IsExpired));

        // Bail échu mais bon pas encore repris : son dernier détenteur peut encore écrire, ce qui renouvelle le bail.
        await Kit.AddLineOkAsync(first, note.Note.Id, product.Id, 15);
        await StockTestKit.AssertErrorAsync(
            await Kit.AcquireAsync(second, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);

        // Nouveau bail court, cette fois laissé échoir : le second employé prend le bon.
        options.LockLease = TimeSpan.FromMilliseconds(150);
        try
        {
            (await Kit.RenewAsync(first, note.Note.Id)).EnsureSuccessStatusCode();
        }
        finally
        {
            options.LockLease = SupplierNoteOptions.DefaultLockLease;
        }

        await Task.Delay(400);
        var taken = await Kit.ReadAsync<SupplierNoteDetail>(await Kit.AcquireAsync(second, note.Note.Id));
        Assert.Equal((secondId, true, false), (taken.Note.Lock!.EmployeeId, taken.Note.Lock.HeldByMe, taken.Note.Lock.IsExpired));

        // L'ancien détenteur ne peut plus rien écrire ni reprendre le bon.
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(first, note.Note.Id, product.Id, 1), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
        await StockTestKit.AssertErrorAsync(
            await Kit.RenewAsync(first, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
        await StockTestKit.AssertErrorAsync(
            await Kit.AcquireAsync(first, note.Note.Id), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);
        await Kit.AddLineOkAsync(second, note.Note.Id, product.Id, 5);
        Assert.Equal(20m, await stock.ReadLevelAsync(product.Id, location));
    }

    [Fact]
    public async Task La_liberation_forcee_exige_le_droit_de_modifier_et_est_auditee()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var (holder, holderId) = await stock.CreateEmployeeClientAsync(AllNoteRights);
        var (clerk, _) = await stock.CreateEmployeeClientAsync(AllNoteRightsExcept(Permissions.NotesEdit));
        var (supervisor, supervisorId) = await stock.CreateEmployeeClientAsync(Permissions.NotesView, Permissions.NotesEdit, Permissions.NotesEnter);
        var note = await notes.CreateNoteAsync(holder, location);

        await StockTestKit.AssertErrorAsync(
            await Kit.ForceReleaseAsync(clerk, note.Note.Id), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.Equal(holderId, (await Kit.ReadNoteAsync(clerk, note.Note.Id)).Note.Lock!.EmployeeId);
        Assert.Equal(0, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LockForced, note.Note.Id));

        var released = await Kit.ReadAsync<SupplierNoteSummary>(await Kit.ForceReleaseAsync(supervisor, note.Note.Id));

        Assert.Null(released.Lock);
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LockForced, note.Note.Id));
        await using (var connection = await server.DataSource.OpenConnectionAsync())
        {
            var audit = await connection.QuerySingleAsync<(Guid EmployeeId, string Details)>(
                "SELECT employee_id, details::text FROM audit_logs WHERE action = @action AND entity_id = @id",
                new { action = SupplierNoteAuditActions.LockForced, id = note.Note.Id.ToString() });
            Assert.Equal(supervisorId, audit.EmployeeId);
            Assert.Contains(holderId.ToString(), audit.Details);
        }

        Assert.Contains((await Kit.ReadNoteAsync(supervisor, note.Note.Id)).History, entry => entry.Event == SupplierNoteEvents.LockForced);

        // Le bon est libre : un autre le prend, et celui qui a été dessaisi est refusé.
        (await Kit.AcquireAsync(supervisor, note.Note.Id)).EnsureSuccessStatusCode();
        await Kit.AddLineOkAsync(supervisor, note.Note.Id, product.Id, 3);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(holder, note.Note.Id, product.Id, 1), HttpStatusCode.Conflict, SupplierNoteErrorCodes.LockedByOther);

        // Forcer un bon déjà libre : sans effet, sans nouvel audit.
        (await Kit.ReleaseAsync(supervisor, note.Note.Id)).EnsureSuccessStatusCode();
        (await Kit.ForceReleaseAsync(supervisor, note.Note.Id)).EnsureSuccessStatusCode();
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.LockForced, note.Note.Id));
    }

    // ---------- Coupure pendant l'enregistrement ----------

    [Fact]
    public async Task Une_coupure_au_milieu_de_l_enregistrement_d_une_ligne_ne_laisse_rien_et_la_commande_rejouee_reussit()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        await stock.SetStockAsync(product.Id, location, 10);
        var note = await notes.CreateNoteAsync(Admin, location);
        var operationId = Guid.NewGuid();
        var auditBefore = await notes.ScalarAsync<int>("SELECT count(*)::int FROM audit_logs");

        // L'enregistrement échoue APRÈS le mouvement de stock et l'écriture de la ligne :
        // l'historique des achats refuse ce produit, comme le ferait une panne à cet instant.
        await using (var connection = await server.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                $"""
                 CREATE FUNCTION simulate_cut() RETURNS trigger LANGUAGE plpgsql AS $$
                 BEGIN
                     IF NEW.product_id = '{product.Id}' THEN
                         RAISE EXCEPTION 'Coupure simulée pendant l''enregistrement de la ligne.';
                     END IF;
                     RETURN NEW;
                 END;
                 $$;
                 CREATE TRIGGER simulate_cut BEFORE INSERT ON purchase_history
                     FOR EACH ROW EXECUTE FUNCTION simulate_cut();
                 """);
        }

        try
        {
            HttpResponseMessage? response = null;
            Exception? failure = null;
            try
            {
                response = await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 15, 800m, operationId);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.True(
                failure is not null || response!.StatusCode == HttpStatusCode.InternalServerError,
                "L'enregistrement aurait dû échouer.");
            if (failure is not null)
            {
                Assert.Contains("Coupure simulée", failure.ToString());
            }

            // Il ne reste ni mouvement, ni ligne, ni achat, ni audit, ni trace de l'opération.
            Assert.Equal(10m, await stock.ReadLevelAsync(product.Id, location));
            Assert.Equal(0, await stock.CountMovementsAsync(product.Id, StockMovementTypes.NoteEntry));
            Assert.Equal(0, await notes.CountNoteMovementsAsync(note.Note.Id));
            Assert.Equal(0, await notes.ScalarAsync<int>(
                "SELECT count(*)::int FROM supplier_note_lines WHERE note_id = @Id", new { note.Note.Id }));
            Assert.Equal(0, await notes.CountPurchasesAsync(product.Id));
            Assert.Equal(0, await notes.ScalarAsync<int>(
                "SELECT count(*)::int FROM operations WHERE operation_id = @operationId", new { operationId }));
            Assert.Equal(auditBefore, await notes.ScalarAsync<int>("SELECT count(*)::int FROM audit_logs"));
            Assert.Empty((await Kit.ReadNoteAsync(Admin, note.Note.Id)).Lines);
            await StockInvariant.AssertHoldsAsync(server.DataSource);
        }
        finally
        {
            await using var connection = await server.DataSource.OpenConnectionAsync();
            await connection.ExecuteAsync("DROP TRIGGER simulate_cut ON purchase_history; DROP FUNCTION simulate_cut();");
        }

        // La même commande, rejouée par le poste, réussit et n'entre qu'une fois en stock.
        var retried = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 15, 800m, operationId));
        var again = await Kit.ReadAsync<SupplierNoteLineResult>(await Kit.AddLineAsync(Admin, note.Note.Id, product.Id, 15, 800m, operationId));
        Assert.Equal(25m, retried.StockAfter);
        Assert.Equal(retried, again);
        Assert.Equal(25m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(product.Id, StockMovementTypes.NoteEntry));
        Assert.Single((await Kit.ReadNoteAsync(Admin, note.Note.Id)).Lines);
    }

    // ---------- Prix d'achat ----------

    [Fact]
    public async Task L_historique_des_achats_garde_chaque_prix_et_une_correction_ajoute_une_entree_sans_reecrire_l_ancienne()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var supplier = await notes.CreateSupplierAsync();
        var note = await notes.CreateNoteAsync(Admin, location, supplier.Id, new DateOnly(2026, 11, 2));
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 15, 800m)).Line;
        (await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 15, 825.50m)).EnsureSuccessStatusCode();
        (await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 2, 12, 825.50m)).EnsureSuccessStatusCode();
        (await Kit.CancelLineAsync(Admin, note.Note.Id, line.Id, 3)).EnsureSuccessStatusCode();

        await using var connection = await server.DataSource.OpenConnectionAsync();
        var history = (await connection.QueryAsync<(string Kind, decimal Price, decimal Quantity, Guid SupplierId, Guid NoteId, Guid LineId, DateTime NoteDate, Guid EmployeeId)>(
            """
            SELECT kind, purchase_price, quantity, supplier_id, note_id, line_id, note_date::timestamp, employee_id
            FROM purchase_history WHERE product_id = @Id ORDER BY id
            """,
            new { product.Id })).ToList();
        Assert.Equal(
            [("ACHAT", 800m, 15m), ("CORRECTION", 825.50m, 15m), ("CORRECTION", 825.50m, 12m), ("ANNULATION", 825.50m, 0m)],
            history.Select(entry => (entry.Kind, entry.Price, entry.Quantity)));
        Assert.All(history, entry => Assert.Equal(
            (supplier.Id, note.Note.Id, line.Id, new DateTime(2026, 11, 2), server.AdminId),
            (entry.SupplierId, entry.NoteId, entry.LineId, entry.NoteDate, entry.EmployeeId)));

        // La base elle-même refuse toute réécriture des historiques.
        var statements = new[]
        {
            "UPDATE purchase_history SET purchase_price = 1",
            "DELETE FROM purchase_history",
            "TRUNCATE purchase_history",
            "UPDATE supplier_note_history SET description = 'x'",
            "DELETE FROM supplier_note_history",
            "TRUNCATE supplier_note_history",
        };
        foreach (var statement in statements)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(statement));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, exception.SqlState);
        }
    }

    [Fact]
    public async Task Le_serveur_fournit_le_dernier_prix_d_achat_connu_du_produit()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync(price: 1200m);
        var firstSupplier = await notes.CreateSupplierAsync();
        var secondSupplier = await notes.CreateSupplierAsync();

        // Jamais acheté : aucun prix connu.
        var unknown = await Kit.ReadProductAsync(Admin, product.Id);
        Assert.Equal((null, null, null), (unknown.LastPurchasePrice, unknown.LastSupplierName, unknown.LastPurchaseDate));
        Assert.Equal((product.Id, product.Code, product.Name, product.Barcode, 1200m, true, true),
            (unknown.Id, unknown.Code, unknown.Name, unknown.Barcode, unknown.SalePrice, unknown.SoldByUnit, unknown.IsActive));

        var firstNote = await notes.CreateNoteAsync(Admin, location, firstSupplier.Id, new DateOnly(2026, 10, 1));
        var firstLine = (await Kit.AddLineOkAsync(Admin, firstNote.Note.Id, product.Id, 10, 800m)).Line;
        Assert.Equal(
            (800m, firstSupplier.Name, new DateOnly(2026, 10, 1)),
            await LastPurchaseAsync(product.Id));

        var secondNote = await notes.CreateNoteAsync(Admin, location, secondSupplier.Id, new DateOnly(2026, 11, 5));
        var secondLine = (await Kit.AddLineOkAsync(Admin, secondNote.Note.Id, product.Id, 10, 850.55m)).Line;
        Assert.Equal((850.55m, secondSupplier.Name, new DateOnly(2026, 11, 5)), await LastPurchaseAsync(product.Id));

        // Même réponse par le code-barres scanné.
        var scanned = await Kit.ReadAsync<NoteProductInfo>(await Admin.GetAsync(SupplierNoteRoutes.ProductInfoByBarcode(product.Barcode!)));
        Assert.Equal(await Kit.ReadProductAsync(Admin, product.Id), scanned);

        // Corriger le prix de la dernière ligne corrige le dernier prix connu ; corriger une ligne plus ancienne non.
        (await Kit.UpdateLineAsync(Admin, secondNote.Note.Id, secondLine.Id, 1, 10, 860m)).EnsureSuccessStatusCode();
        Assert.Equal(860m, (await LastPurchaseAsync(product.Id)).Price);
        (await Kit.UpdateLineAsync(Admin, firstNote.Note.Id, firstLine.Id, 1, 10, 790m)).EnsureSuccessStatusCode();
        Assert.Equal(860m, (await LastPurchaseAsync(product.Id)).Price);

        // Une ligne annulée, puis un bon annulé, ne comptent plus.
        (await Kit.CancelLineAsync(Admin, secondNote.Note.Id, secondLine.Id, 2)).EnsureSuccessStatusCode();
        Assert.Equal((790m, firstSupplier.Name, new DateOnly(2026, 10, 1)), await LastPurchaseAsync(product.Id));
        (await Kit.CancelNoteAsync(Admin, firstNote.Note.Id)).EnsureSuccessStatusCode();
        Assert.Null((await Kit.ReadProductAsync(Admin, product.Id)).LastPurchasePrice);

        await StockTestKit.AssertErrorAsync(
            await Admin.GetAsync(SupplierNoteRoutes.ProductInfo(Guid.NewGuid())), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    private async Task<(decimal? Price, string? Supplier, DateOnly? Date)> LastPurchaseAsync(Guid productId)
    {
        var info = await Kit.ReadProductAsync(Admin, productId);
        return (info.LastPurchasePrice, info.LastSupplierName, info.LastPurchaseDate);
    }

    [Fact]
    public async Task La_variation_de_marge_est_calculee_exactement()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync(price: 1200m);
        var firstNote = await notes.CreateNoteAsync(Admin, location);

        // Premier achat : aucun prix précédent, donc pas de marge « avant ».
        var first = (await Kit.AddLineOkAsync(Admin, firstNote.Note.Id, product.Id, 10, 800m)).Line;
        Assert.Equal(new MarginVariation(1200m, null, 800m, null, null, 400m, 33.33m), first.Margin);

        // Deuxième achat plus cher : 1200 − 850,55 = 349,45 ; 349,45 / 1200 = 29,1208… %.
        var secondNote = await notes.CreateNoteAsync(Admin, location);
        var second = (await Kit.AddLineOkAsync(Admin, secondNote.Note.Id, product.Id, 10, 850.55m)).Line;
        Assert.Equal(new MarginVariation(1200m, 800m, 850.55m, 400m, 33.33m, 349.45m, 29.12m), second.Margin);

        // Corriger le prix de la ligne : « avant » reste le prix connu à l'enregistrement.
        var corrected = (await Kit.ReadAsync<SupplierNoteLineResult>(
            await Kit.UpdateLineAsync(Admin, secondNote.Note.Id, second.Id, 1, 10, 1300m))).Line;
        Assert.Equal(new MarginVariation(1200m, 800m, 1300m, 400m, 33.33m, -100m, -8.33m), corrected.Margin);
        Assert.Equal(corrected.Margin, (await Kit.ReadNoteAsync(Admin, secondNote.Note.Id)).Lines.Single().Margin);

        // Arrondi au centième, demi vers le haut : 1 / 800 = 0,125 % → 0,13 % ; 666,66 / 999,99 = 66,666… % → 66,67 %.
        var half = await stock.CreateProductAsync(price: 800m);
        Assert.Equal(
            new MarginVariation(800m, null, 799m, null, null, 1m, 0.13m),
            (await Kit.AddLineOkAsync(Admin, secondNote.Note.Id, half.Id, 1, 799m)).Line.Margin);
        var third = await stock.CreateProductAsync(price: 999.99m);
        Assert.Equal(
            new MarginVariation(999.99m, null, 333.33m, null, null, 666.66m, 66.67m),
            (await Kit.AddLineOkAsync(Admin, secondNote.Note.Id, third.Id, 1, 333.33m)).Line.Margin);

        // Prix de vente nul : marge en montant, pas de pourcentage.
        var free = await stock.CreateProductAsync(price: 0m);
        Assert.Equal(
            new MarginVariation(0m, null, 50m, null, null, -50m, null),
            (await Kit.AddLineOkAsync(Admin, secondNote.Note.Id, free.Id, 1, 50m)).Line.Margin);

        // Total de ligne exact avec une quantité décimale : 2,375 × 333,33 = 791,65875 → 791,66.
        var bulk = await stock.CreateProductAsync(soldByUnit: false);
        Assert.Equal(791.66m, (await Kit.AddLineOkAsync(Admin, secondNote.Note.Id, bulk.Id, 2.375m, 333.33m)).Line.LineTotal);
        Assert.Equal(2.375m, await stock.ReadLevelAsync(bulk.Id, location));
    }

    [Fact]
    public async Task Un_bon_ne_modifie_jamais_le_prix_de_vente()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync(price: 1200m);
        var note = await notes.CreateNoteAsync(Admin, location);

        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 10, 800m)).Line;
        (await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 12, 1500m)).EnsureSuccessStatusCode();
        await Kit.AddLineOkAsync(Admin, note.Note.Id, product.Id, 3, 2000m);
        (await Kit.ValidateAsync(Admin, note.Note.Id)).EnsureSuccessStatusCode();
        (await Kit.AcquireAsync(Admin, note.Note.Id)).EnsureSuccessStatusCode();
        (await Kit.CancelNoteAsync(Admin, note.Note.Id)).EnsureSuccessStatusCode();

        Assert.Equal(1200m, await notes.ScalarAsync<decimal>("SELECT sale_price FROM products WHERE id = @Id", new { product.Id }));
        // Seule l'entrée de création du produit figure dans l'historique des prix de vente.
        Assert.Equal(1, await notes.ScalarAsync<int>(
            "SELECT count(*)::int FROM product_sale_prices WHERE product_id = @Id", new { product.Id }));
        Assert.Equal(0, await stock.CountAuditAsync(AuditActions.SalePriceChanged, product.Id.ToString()));
    }

    // ---------- Produit inconnu scanné ----------

    [Fact]
    public async Task Un_code_barres_inconnu_est_signale_puis_le_produit_cree_s_ajoute_au_bon()
    {
        var location = await stock.MainLocationAsync();
        var (clerk, _) = await stock.CreateEmployeeClientAsync(Permissions.NotesEnter, Permissions.ProductsCreate);
        var (clerkWithoutCreation, _) = await stock.CreateEmployeeClientAsync(Permissions.NotesEnter);
        var supplier = await Kit.ReadAsync<SupplierSummary>(await clerk.PostAsJsonAsync(
            SupplierNoteRoutes.Suppliers, new CreateSupplierRequest(Guid.NewGuid(), $"Fournisseur créé à la volée {Guid.NewGuid():N}")));
        var note = await Kit.ReadAsync<SupplierNoteDetail>(await Kit.PostNoteAsync(clerk, supplier.Id, location));
        var barcode = StockTestKit.NewBarcode();

        // 1. Le code scanné est inconnu : réponse exploitable par l'écran.
        var unknown = await StockTestKit.AssertErrorAsync(
            await clerk.GetAsync(SupplierNoteRoutes.ProductInfoByBarcode(barcode)), HttpStatusCode.NotFound, SupplierNoteErrorCodes.UnknownBarcode);
        Assert.Contains(barcode, unknown.Message);

        // 2. Création rapide par la route existante : refusée sans le droit de créer un produit.
        var creation = new CreateProductRequest(Guid.NewGuid(), "Produit créé pendant la saisie", [barcode], null, null, 1500m, true);
        await StockTestKit.AssertErrorAsync(
            await clerkWithoutCreation.PostAsJsonAsync(ApiRoutes.Products, creation), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        var created = await Kit.ReadAsync<ProductSummary>(await clerk.PostAsJsonAsync(ApiRoutes.Products, creation));

        // 3. Retour au bon : le même code est maintenant trouvé, et la ligne s'ajoute.
        var found = await Kit.ReadAsync<NoteProductInfo>(await clerk.GetAsync(SupplierNoteRoutes.ProductInfoByBarcode(barcode)));
        Assert.Equal((created.Id, "Produit créé pendant la saisie", 1500m, null), (found.Id, found.Name, found.SalePrice, found.LastPurchasePrice));
        var result = await Kit.AddLineOkAsync(clerk, note.Note.Id, found.Id, 15, 900m);

        Assert.Equal(15m, result.StockAfter);
        Assert.Equal(barcode, result.Line.Barcode);
        Assert.Equal(15m, await stock.ReadLevelAsync(created.Id, location));
        Assert.Equal(900m, (await Kit.ReadProductAsync(clerk, created.Id)).LastPurchasePrice);
    }

    // ---------- Fournisseurs ----------

    [Fact]
    public async Task Un_nom_de_fournisseur_est_unique_sans_tenir_compte_de_la_casse()
    {
        var name = $"Laboratoires Essai {Guid.NewGuid():N}";
        var operationId = Guid.NewGuid();
        var request = new CreateSupplierRequest(operationId, $"  {name}  ");

        var created = await Kit.ReadAsync<SupplierSummary>(await Admin.PostAsJsonAsync(SupplierNoteRoutes.Suppliers, request));
        var replay = await Kit.ReadAsync<SupplierSummary>(await Admin.PostAsJsonAsync(SupplierNoteRoutes.Suppliers, request));
        Assert.Equal(name, created.Name);
        Assert.Equal(created, replay);

        var duplicate = await StockTestKit.AssertErrorAsync(
            await Admin.PostAsJsonAsync(SupplierNoteRoutes.Suppliers, new CreateSupplierRequest(Guid.NewGuid(), name.ToUpperInvariant())),
            HttpStatusCode.Conflict, ApiErrorCodes.Conflict);
        Assert.Contains("existe déjà", duplicate.Message);
        await StockTestKit.AssertErrorAsync(
            await Admin.PostAsJsonAsync(SupplierNoteRoutes.Suppliers, new CreateSupplierRequest(Guid.NewGuid(), "   ")),
            HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);

        // Créations simultanées du même nom : une seule réussit.
        var raced = $"Course {Guid.NewGuid():N}";
        var responses = await InParallelAsync(8, index => Admin.PostAsJsonAsync(
            SupplierNoteRoutes.Suppliers, new CreateSupplierRequest(Guid.NewGuid(), index % 2 == 0 ? raced : raced.ToUpperInvariant())));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        var all = await Kit.ReadAsync<List<SupplierSummary>>(await Admin.GetAsync(SupplierNoteRoutes.Suppliers));
        Assert.Single(all, supplier => supplier.Id == created.Id);
        var filtered = await Kit.ReadAsync<List<SupplierSummary>>(
            await Admin.GetAsync($"{SupplierNoteRoutes.Suppliers}?q={Uri.EscapeDataString(name.ToUpperInvariant())}"));
        Assert.Equal([created], filtered);
        Assert.Equal(1, await stock.CountAuditAsync(SupplierNoteAuditActions.SupplierCreated, created.Id.ToString()));
    }

    // ---------- Création, liste et lecture ----------

    [Fact]
    public async Task La_creation_d_un_bon_controle_ses_donnees_et_ne_se_fait_qu_une_fois()
    {
        var location = await stock.MainLocationAsync();
        var supplier = await notes.CreateSupplierAsync();
        var operationId = Guid.NewGuid();

        var created = await Kit.ReadAsync<SupplierNoteDetail>(
            await Kit.PostNoteAsync(Admin, supplier.Id, location, "  BL-2026-118  ", new DateOnly(2026, 11, 14), operationId));
        var replay = await Kit.ReadAsync<SupplierNoteDetail>(
            await Kit.PostNoteAsync(Admin, supplier.Id, location, "  BL-2026-118  ", new DateOnly(2026, 11, 14), operationId));

        Assert.Equal(created.Note.Id, replay.Note.Id);
        Assert.Equal(
            (supplier.Id, supplier.Name, location, server.StoreId, "BL-2026-118", new DateOnly(2026, 11, 14), SupplierNoteStatuses.InProgress, 0, 0m),
            (created.Note.SupplierId, created.Note.SupplierName, created.Note.LocationId, created.Note.StoreId, created.Note.SupplierReference,
                created.Note.NoteDate, created.Note.Status, created.Note.LineCount, created.Note.TotalAmount));
        Assert.Equal("Administratrice Test", created.Note.CreatedByName);
        Assert.Equal(1, await notes.ScalarAsync<int>(
            "SELECT count(*)::int FROM supplier_notes WHERE supplier_id = @Id", new { supplier.Id }));
        Assert.Equal(1, await notes.CountNoteAuditAsync(SupplierNoteAuditActions.NoteCreated, created.Note.Id));

        await StockTestKit.AssertErrorAsync(
            await Kit.PostNoteAsync(Admin, supplier.Id, location, "   "), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.PostNoteAsync(Admin, Guid.NewGuid(), location), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await Kit.PostNoteAsync(Admin, supplier.Id, Guid.NewGuid()), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await Kit.PostNoteAsync(Admin, supplier.Id, location, operationId: Guid.Empty), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Admin.GetAsync(SupplierNoteRoutes.Note(Guid.NewGuid())), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    [Fact]
    public async Task La_liste_des_bons_se_filtre_par_etat_fournisseur_et_dates_et_se_pagine()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var supplier = await notes.CreateSupplierAsync();
        var october = await notes.CreateNoteAsync(Admin, location, supplier.Id, new DateOnly(2026, 10, 10));
        var early = await notes.CreateNoteAsync(Admin, location, supplier.Id, new DateOnly(2026, 11, 3));
        var late = await notes.CreateNoteAsync(Admin, location, supplier.Id, new DateOnly(2026, 11, 12));
        await notes.CreateNoteAsync(Admin, location, date: new DateOnly(2026, 11, 12));
        await Kit.AddLineOkAsync(Admin, early.Note.Id, product.Id, 3, 100.10m);
        (await Kit.ValidateAsync(Admin, early.Note.Id)).EnsureSuccessStatusCode();
        (await Kit.CancelNoteAsync(Admin, october.Note.Id)).EnsureSuccessStatusCode();

        async Task<SupplierNotePage> ListAsync(string query) =>
            await Kit.ReadAsync<SupplierNotePage>(await Admin.GetAsync($"{SupplierNoteRoutes.Notes}?fournisseur={supplier.Id}{query}"));

        var all = await ListAsync(string.Empty);
        Assert.Equal(3, all.Total);
        Assert.Equal([late.Note.Id, early.Note.Id, october.Note.Id], all.Items.Select(note => note.Id));
        var validated = Assert.Single((await ListAsync("&etat=VALIDE")).Items);
        Assert.Equal((early.Note.Id, 1, 300.30m), (validated.Id, validated.LineCount, validated.TotalAmount));
        Assert.Equal([october.Note.Id], (await ListAsync("&etat=annule")).Items.Select(note => note.Id));
        Assert.Equal([late.Note.Id], (await ListAsync("&etat=EN_SAISIE")).Items.Select(note => note.Id));
        Assert.Equal([late.Note.Id, early.Note.Id], (await ListAsync("&du=2026-11-01")).Items.Select(note => note.Id));
        Assert.Equal([early.Note.Id, october.Note.Id], (await ListAsync("&au=2026-11-03")).Items.Select(note => note.Id));
        Assert.Equal([early.Note.Id], (await ListAsync("&du=2026-11-03&au=2026-11-03")).Items.Select(note => note.Id));

        var firstPage = await ListAsync("&taille=2");
        var secondPage = await ListAsync("&taille=2&page=2");
        Assert.Equal((3, 1, 2, 2), (firstPage.Total, firstPage.Page, firstPage.PageSize, firstPage.Items.Count));
        Assert.Equal([october.Note.Id], secondPage.Items.Select(note => note.Id));

        // Sans filtre de fournisseur, le bon de l'autre fournisseur apparaît aussi.
        var everything = await Kit.ReadAsync<SupplierNotePage>(await Admin.GetAsync($"{SupplierNoteRoutes.Notes}?du=2026-11-12&au=2026-11-12&taille=200"));
        Assert.True(everything.Total >= 2);
        await StockTestKit.AssertErrorAsync(
            await Admin.GetAsync($"{SupplierNoteRoutes.Notes}?etat=INCONNU"), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task Une_ligne_invalide_est_refusee_sans_toucher_au_stock()
    {
        var location = await stock.MainLocationAsync();
        var unit = await stock.CreateProductAsync(soldByUnit: true);
        var inactive = await stock.CreateProductAsync("Produit désactivé d'essai");
        (await Admin.PutAsJsonAsync(
            ApiRoutes.Product(inactive.Id),
            new UpdateProductRequest(Guid.NewGuid(), inactive.Name, null, null, false, null, null))).EnsureSuccessStatusCode();
        var note = await notes.CreateNoteAsync(Admin, location);
        var line = (await Kit.AddLineOkAsync(Admin, note.Note.Id, unit.Id, 4)).Line;

        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, unit.Id, 0), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, unit.Id, -3), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, unit.Id, 1.5m), HttpStatusCode.BadRequest, ApiErrorCodes.FractionalQuantity);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, unit.Id, 1, -1m), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, unit.Id, 1, 10.001m), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, Guid.NewGuid(), 1), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, inactive.Id, 1), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.AddLineAsync(Admin, note.Note.Id, unit.Id, 1, operationId: Guid.Empty), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 0), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, note.Note.Id, line.Id, 1, 2.5m), HttpStatusCode.BadRequest, ApiErrorCodes.FractionalQuantity);
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, note.Note.Id, Guid.NewGuid(), 1, 2), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

        // Une ligne d'un autre bon n'est pas modifiable par ce bon.
        var otherNote = await notes.CreateNoteAsync(Admin, location);
        await StockTestKit.AssertErrorAsync(
            await Kit.UpdateLineAsync(Admin, otherNote.Note.Id, line.Id, 1, 2), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

        Assert.Equal(4m, await stock.ReadLevelAsync(unit.Id, location));
        Assert.Equal(1, await stock.CountMovementsAsync(unit.Id));
        Assert.Equal(0, await stock.CountMovementsAsync(inactive.Id));
        Assert.Single((await Kit.ReadNoteAsync(Admin, note.Note.Id)).Lines);
    }

    // ---------- Permissions ----------

    [Fact]
    public async Task Chaque_route_des_bons_est_refusee_sans_son_droit()
    {
        var location = await stock.MainLocationAsync();
        var product = await stock.CreateProductAsync();
        var supplier = await notes.CreateSupplierAsync();
        var inProgress = await notes.CreateNoteAsync(Admin, location, supplier.Id);
        var openLine = (await Kit.AddLineOkAsync(Admin, inProgress.Note.Id, product.Id, 15)).Line;
        var validated = await notes.CreateNoteAsync(Admin, location, supplier.Id);
        var validatedLine = (await Kit.AddLineOkAsync(Admin, validated.Note.Id, product.Id, 5)).Line;
        (await Kit.ValidateAsync(Admin, validated.Note.Id)).EnsureSuccessStatusCode();

        var (noNoteRight, _) = await stock.CreateEmployeeClientAsync(Permissions.ProductsView, Permissions.StockAdjust);
        var (viewOnly, _) = await stock.CreateEmployeeClientAsync(Permissions.NotesView);
        var (noEnter, _) = await stock.CreateEmployeeClientAsync(AllNoteRightsExcept(Permissions.NotesEnter));
        var (noEdit, _) = await stock.CreateEmployeeClientAsync(AllNoteRightsExcept(Permissions.NotesEdit));
        var (noValidate, _) = await stock.CreateEmployeeClientAsync(AllNoteRightsExcept(Permissions.NotesValidate));
        var (noCancel, _) = await stock.CreateEmployeeClientAsync(AllNoteRightsExcept(Permissions.NotesCancel));
        var noteId = inProgress.Note.Id;
        var validatedId = validated.Note.Id;

        var attempts = new (string Route, Func<Task<HttpResponseMessage>> Send)[]
        {
            // Lecture : au moins un droit sur les bons.
            ("GET fournisseurs", () => noNoteRight.GetAsync(SupplierNoteRoutes.Suppliers)),
            ("GET bons", () => noNoteRight.GetAsync(SupplierNoteRoutes.Notes)),
            ("GET bon", () => noNoteRight.GetAsync(SupplierNoteRoutes.Note(noteId))),
            ("GET produit", () => noNoteRight.GetAsync(SupplierNoteRoutes.ProductInfo(product.Id))),
            ("GET code-barres", () => noNoteRight.GetAsync(SupplierNoteRoutes.ProductInfoByBarcode(product.Barcode!))),

            // Saisie : « bons.saisir ».
            ("POST fournisseurs", () => noEnter.PostAsJsonAsync(SupplierNoteRoutes.Suppliers, new CreateSupplierRequest(Guid.NewGuid(), "Refusé"))),
            ("POST bons", () => Kit.PostNoteAsync(noEnter, supplier.Id, location)),
            ("POST ligne", () => Kit.AddLineAsync(noEnter, noteId, product.Id, 1)),
            ("PUT ligne", () => Kit.UpdateLineAsync(noEnter, noteId, openLine.Id, 1, 2)),
            ("POST annulation de ligne", () => Kit.CancelLineAsync(noEnter, noteId, openLine.Id, 1)),

            // Correction d'un bon validé et libération forcée : « bons.modifier ».
            ("POST ligne (bon validé)", () => Kit.AddLineAsync(noEdit, validatedId, product.Id, 1)),
            ("PUT ligne (bon validé)", () => Kit.UpdateLineAsync(noEdit, validatedId, validatedLine.Id, 1, 2)),
            ("POST annulation de ligne (bon validé)", () => Kit.CancelLineAsync(noEdit, validatedId, validatedLine.Id, 1)),
            ("POST libération forcée", () => Kit.ForceReleaseAsync(noEdit, noteId)),

            ("POST validation", () => Kit.ValidateAsync(noValidate, noteId)),
            ("POST annulation", () => Kit.CancelNoteAsync(noCancel, noteId)),

            // Verrou : au moins un droit d'écriture sur les bons.
            ("POST verrou", () => Kit.AcquireAsync(viewOnly, noteId)),
            ("PUT verrou", () => Kit.RenewAsync(viewOnly, noteId)),
            ("DELETE verrou", () => Kit.ReleaseAsync(viewOnly, noteId)),
        };

        var deniedBefore = await notes.ScalarAsync<int>(
            "SELECT count(*)::int FROM audit_logs WHERE action = @action", new { action = AuditActions.PermissionDenied });
        foreach (var (route, send) in attempts)
        {
            var response = await send();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} : {(int)response.StatusCode} au lieu de 403.");
            Assert.Equal(ApiErrorCodes.PermissionDenied, (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }

        // Chaque refus est audité, et rien n'a été écrit.
        Assert.Equal(deniedBefore + attempts.Length, await notes.ScalarAsync<int>(
            "SELECT count(*)::int FROM audit_logs WHERE action = @action", new { action = AuditActions.PermissionDenied }));
        Assert.Equal(20m, await stock.ReadLevelAsync(product.Id, location));
        Assert.Equal(2, await stock.CountMovementsAsync(product.Id));
        var read = await Kit.ReadNoteAsync(Admin, noteId);
        Assert.Equal((SupplierNoteStatuses.InProgress, server.AdminId), (read.Note.Status, read.Note.Lock!.EmployeeId));
        Assert.Equal(1, Assert.Single(read.Lines).Version);
        Assert.Equal(SupplierNoteStatuses.Validated, (await Kit.ReadNoteAsync(Admin, validatedId)).Note.Status);
        Assert.Equal(0, await notes.ScalarAsync<int>("SELECT count(*)::int FROM suppliers WHERE name = 'Refusé'"));

        // Sans employé identifié : aucune route ne répond.
        var poste = await server.CreatePosteClientAsync();
        foreach (var send in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => poste.GetAsync(SupplierNoteRoutes.Notes),
                     () => poste.GetAsync(SupplierNoteRoutes.Note(noteId)),
                     () => Kit.AddLineAsync(poste, noteId, product.Id, 1),
                     () => Kit.AcquireAsync(poste, noteId),
                     () => Kit.ValidateAsync(poste, noteId),
                 })
        {
            await StockTestKit.AssertErrorAsync(await send(), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
        }

        // Qui peut saisir peut lire le bon qu'il saisit, même sans « bons.consulter ».
        var (enterOnly, _) = await stock.CreateEmployeeClientAsync(Permissions.NotesEnter);
        Assert.Equal(noteId, (await Kit.ReadNoteAsync(enterOnly, noteId)).Note.Id);
    }

    [Fact]
    public async Task Un_droit_limite_a_un_site_ne_vaut_pas_pour_un_bon_d_un_autre_site()
    {
        var here = await stock.MainLocationAsync();
        var elsewhere = await stock.OtherStoreLocationAsync();
        var product = await stock.CreateProductAsync();
        var supplier = await notes.CreateSupplierAsync();
        var (local, _) = await stock.CreateScopedEmployeeClientAsync(
            AllNoteRights.Select(code => new PermissionGrant(code, server.StoreId)).ToArray());
        var foreign = await notes.CreateNoteAsync(Admin, elsewhere, supplier.Id);
        var foreignLine = (await Kit.AddLineOkAsync(Admin, foreign.Note.Id, product.Id, 5)).Line;
        (await Kit.ReleaseAsync(Admin, foreign.Note.Id)).EnsureSuccessStatusCode();

        // Sur son site : tout est permis.
        var mine = await Kit.ReadAsync<SupplierNoteDetail>(await Kit.PostNoteAsync(local, supplier.Id, here));
        await Kit.AddLineOkAsync(local, mine.Note.Id, product.Id, 2);

        // Sur l'autre site : ni création, ni lecture, ni verrou, ni écriture.
        var attempts = new[]
        {
            await Kit.PostNoteAsync(local, supplier.Id, elsewhere),
            await local.GetAsync(SupplierNoteRoutes.Note(foreign.Note.Id)),
            await Kit.AcquireAsync(local, foreign.Note.Id),
            await Kit.AddLineAsync(local, foreign.Note.Id, product.Id, 1),
            await Kit.UpdateLineAsync(local, foreign.Note.Id, foreignLine.Id, 1, 2),
            await Kit.ValidateAsync(local, foreign.Note.Id),
            await Kit.CancelNoteAsync(local, foreign.Note.Id),
            await Kit.ForceReleaseAsync(local, foreign.Note.Id),
        };
        foreach (var response in attempts)
        {
            await StockTestKit.AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        }

        // La liste ne montre que les bons des sites couverts.
        var listed = await Kit.ReadAsync<SupplierNotePage>(await local.GetAsync($"{SupplierNoteRoutes.Notes}?fournisseur={supplier.Id}"));
        Assert.Equal([mine.Note.Id], listed.Items.Select(note => note.Id));
        Assert.Equal(5m, await stock.ReadLevelAsync(product.Id, elsewhere));
        Assert.Equal(2m, await stock.ReadLevelAsync(product.Id, here));
    }
}
