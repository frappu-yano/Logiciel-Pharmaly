using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Closures;

namespace Pharmaly.Tests.Server;

[Collection(SalesTestKit.Collection)]
public sealed class ClosureTests(ServerFixture server) : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private const string Resellable = "Retour revendable";

    private readonly ClosureTestKit kit = new(server);

    public ValueTask InitializeAsync()
    {
        server.Clock.Offset = TimeSpan.Zero;
        return ValueTask.CompletedTask;
    }

    /// <summary>Après chaque scénario : stock et argent cohérents, sur toute la base.</summary>
    public async ValueTask DisposeAsync()
    {
        await StockInvariant.AssertHoldsAsync(server.DataSource);
        await MoneyInvariant.AssertHoldsAsync(server.DataSource);
    }

    private static async Task<ClosureBlockedError> AssertBlockedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ClosureBlockedError>())!;
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }

    private Task<long> CountClosuresAsync(Guid storeId) =>
        kit.Sales.ScalarAsync<long>("SELECT count(*) FROM daily_closures WHERE store_id = @storeId", new { storeId });

    private async Task<long> CountStampedAsync(Guid storeId)
    {
        var total = 0L;
        foreach (var table in ClosureTestKit.DayTables)
        {
            total += await kit.Sales.ScalarAsync<long>(
                $"SELECT count(*) FROM {table} WHERE store_id = @storeId AND business_date IS NOT NULL", new { storeId });
        }

        return total;
    }

    private Task<DateTime?> BusinessDateAsync(string table, string key, Guid id) =>
        kit.Sales.ScalarAsync<DateTime?>($"SELECT business_date FROM {table} WHERE {key} = @id", new { id });

    private static DateTime AsDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    // ---------- Fermeture d'une caisse ----------

    [Fact]
    public async Task La_fermeture_d_une_caisse_enregistre_les_especes_attendues_le_comptage_et_l_ecart_exact()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var customer = await kit.Sales.CreateCustomerAsync();
        var twoUnits = await kit.SellAsync(poste, store, price: 600m, quantity: 2);                      // espèces +1 200
        var card = await kit.SellAsync(poste, store, price: 800m, mode: SalePaymentModes.Card);          // carte, pas d'espèces
        (await SalesTestKit.CardResultAsync(poste, card.Id, accepted: true)).EnsureSuccessStatusCode();
        var debt = await kit.SellAsync(poste, store, price: 700m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);
        await kit.SellAsync(poste, store, price: 500m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);   // reste impayée
        (await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 700m, null, debt.Receivable!.Id)).EnsureSuccessStatusCode(); // +700
        (await poste.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.In, 300m, "Monnaie")))
            .EnsureSuccessStatusCode();                                                                  // +300
        (await poste.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.Out, 150m, "Achat de sacs")))
            .EnsureSuccessStatusCode();                                                                  // −150
        var cancelled = await kit.SellAsync(poste, store, price: 400m);                                  // +400
        (await SalesTestKit.CancelAsync(poste, cancelled.Id)).EnsureSuccessStatusCode();                 // −400
        await kit.ReturnOkAsync(poste, twoUnits, Resellable);                                            // −600
        const decimal Expected = 1000m + 1200m + 700m + 300m - 150m + 400m - 400m - 600m;                // 2 450
        var session = (await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session!;

        // Montant compté invalide : refusé, la caisse reste ouverte.
        foreach (var invalid in new[] { -1m, 10.001m })
        {
            await StockTestKit.AssertErrorAsync(
                await ClosureTestKit.CloseSessionAsync(poste, invalid), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        var operationId = Guid.NewGuid();
        var closed = await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(poste, 2440m, operationId));

        Assert.Equal((session.Id, "FERMEE"), (closed.Id, closed.Status));
        Assert.Equal(1000m, closed.OpeningFloat);
        Assert.Equal((1600m, 800m), (closed.SaleCash, closed.SaleCard));
        Assert.Equal((700m, 0m), (closed.SettlementCash, closed.SettlementCard));
        Assert.Equal((300m, 150m), (closed.CashIn, closed.CashOut));
        Assert.Equal((400m, 0m), (closed.CancellationCashRefunds, closed.CancellationCardRefunds));
        Assert.Equal((600m, 0m), (closed.ReturnCashRefunds, closed.ReturnCardRefunds));
        Assert.Equal(Expected, closed.ExpectedCash);
        Assert.Equal(2440m, closed.CountedCash);
        Assert.Equal(-10m, closed.CashDifference);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal("Administratrice Test", closed.ClosedByName);

        // En base : attendu, compté et écart, avec l'employé qui a compté.
        Assert.Equal(
            "FERMEE 2450.00 2440.00 -10.00",
            await kit.Sales.ScalarAsync<string>(
                """
                SELECT status || ' ' || expected_cash || ' ' || counted_cash || ' ' || cash_difference
                FROM cash_sessions WHERE id = @Id AND closed_by = @AdminId AND closed_at IS NOT NULL
                """,
                new { session.Id, server.AdminId }));
        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ClosureAuditActions.SessionClosed, session.Id.ToString()));

        // Rejouer la même fermeture ne change rien.
        Assert.Equal(
            JsonSerializer.Serialize(closed),
            JsonSerializer.Serialize(await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(poste, 2440m, operationId))));
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseSessionAsync(poste, 2440m), HttpStatusCode.Conflict, CashErrorCodes.NoOpenSession);

        // Une caisse fermée n'accepte plus aucune opération.
        Assert.Null((await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session);
        var product = await kit.ProductAsync(store, 100m);
        var refusals = new[]
        {
            await SalesTestKit.SellAsync(poste, SalesTestKit.Cash(product.Id)),
            await SalesTestKit.SellAsync(poste, SalesTestKit.Unpaid(product.Id, customer.Id)),
            await poste.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.In, 10m, "Monnaie")),
            await SalesTestKit.CancelAsync(poste, twoUnits.Id),
            await ClosureTestKit.ReturnFirstLineAsync(poste, twoUnits, (await kit.ReasonAsync(Resellable)).Id),
            await poste.GetAsync(CashRoutes.SessionSummary),
        };
        foreach (var refusal in refusals)
        {
            await StockTestKit.AssertErrorAsync(refusal, HttpStatusCode.Conflict, CashErrorCodes.NoOpenSession);
        }

        // Une nouvelle session peut être ouverte ensuite : c'est une autre session, vierge.
        var reopened = await ClosureTestKit.OpenAsync(poste, 500m);
        Assert.NotEqual(session.Id, reopened.Id);
        Assert.Equal(500m, (await kit.Sales.SummaryAsync(poste)).ExpectedCash);
    }

    [Fact]
    public async Task La_fermeture_d_une_caisse_est_refusee_tant_qu_un_paiement_TPE_est_en_attente()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var customer = await kit.Sales.CreateCustomerAsync("Client Carte En Attente");
        var debt = await kit.SellAsync(poste, store, price: 2500m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);
        var pendingSale = await kit.SellAsync(poste, store, price: 900m, mode: SalePaymentModes.Card);
        var pendingSettlement = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(poste, PaymentMethods.Card, 2500m, null, debt.Receivable!.Id));

        // Refus, avec la liste de ce qui bloque.
        var error = await AssertBlockedAsync(await ClosureTestKit.CloseSessionAsync(poste, 1000m), ClosureErrorCodes.CardPending);
        Assert.Equal(2, error.Blockers.Count);
        var saleBlocker = Assert.Single(error.Blockers, blocker => blocker.Kind == ClosureBlockerKinds.SaleCardPending);
        Assert.Equal((pendingSale.Id, 900m), (saleBlocker.Reference, saleBlocker.Amount));
        Assert.Contains(pendingSale.TicketNumber, saleBlocker.Message);
        var settlementBlocker = Assert.Single(error.Blockers, blocker => blocker.Kind == ClosureBlockerKinds.SettlementCardPending);
        Assert.Equal((pendingSettlement.Id, 2500m), (settlementBlocker.Reference, settlementBlocker.Amount));
        Assert.Contains("Client Carte En Attente", settlementBlocker.Message);
        Assert.NotNull((await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session);

        // L'état de la journée montre les mêmes blocages, plus la caisse ouverte.
        var day = await ClosureTestKit.DayAsync(poste);
        Assert.False(day.CanClose);
        Assert.Equal(
            new[] { ClosureBlockerKinds.SessionOpen, ClosureBlockerKinds.SaleCardPending, ClosureBlockerKinds.SettlementCardPending },
            day.Blockers.Select(blocker => blocker.Kind));
        Assert.Equal(2, Assert.Single(day.Preview.Sessions).PendingCardCount);
        // Et la journée ne se clôture pas non plus.
        var dayError = await AssertBlockedAsync(await ClosureTestKit.CloseDayAsync(poste), ClosureErrorCodes.DayBlocked);
        Assert.Equal(3, dayError.Blockers.Count);

        // Une seule réponse ne suffit pas.
        (await SalesTestKit.CardResultAsync(poste, pendingSale.Id, accepted: false)).EnsureSuccessStatusCode();
        error = await AssertBlockedAsync(await ClosureTestKit.CloseSessionAsync(poste, 1000m), ClosureErrorCodes.CardPending);
        Assert.Equal(ClosureBlockerKinds.SettlementCardPending, Assert.Single(error.Blockers).Kind);

        // Tout a reçu sa réponse : la caisse se ferme, sans rien compter du paiement refusé.
        (await SalesTestKit.SettlementCardResultAsync(poste, pendingSettlement.Id, accepted: true)).EnsureSuccessStatusCode();
        var closed = await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(poste, 1000m));
        Assert.Equal((1000m, 0m, 0m, 2500m, 0), (closed.ExpectedCash, closed.CashDifference, closed.SaleCard, closed.SettlementCard, closed.PendingCardCount));
    }

    // ---------- Clôture de la journée ----------

    [Fact]
    public async Task La_cloture_est_refusee_tant_qu_une_caisse_du_magasin_est_ouverte()
    {
        var store = await kit.CreateStoreAsync();
        var first = await kit.CreateRegisterClientAsync(store, 1000m);
        var second = await kit.CreateRegisterClientAsync(store, 500m);
        await kit.SellAsync(first, store, price: 250m);
        await kit.SellAsync(second, store, price: 350m);
        await ClosureTestKit.CloseSessionOkAsync(first);

        var error = await AssertBlockedAsync(await ClosureTestKit.CloseDayAsync(first), ClosureErrorCodes.DayBlocked);

        var blocker = Assert.Single(error.Blockers);
        Assert.Equal(ClosureBlockerKinds.SessionOpen, blocker.Kind);
        Assert.Equal((await second.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session!.Id, blocker.Reference);
        // Rien n'est figé, aucune clôture n'existe.
        Assert.Equal(0, await CountClosuresAsync(store.Id));
        Assert.Equal(0, await CountStampedAsync(store.Id));
        var day = await ClosureTestKit.DayAsync(first);
        Assert.False(day.CanClose);
        Assert.Null(day.LastClosedDate);
        Assert.Equal(new[] { "FERMEE", "OUVERTE" }, day.Preview.Sessions.Select(session => session.Status).Order());
        Assert.Equal(600m, day.Preview.Revenue.SalesTotal);

        // La dernière caisse fermée, la clôture passe — depuis un poste dont la caisse est fermée.
        await ClosureTestKit.CloseSessionOkAsync(second);
        Assert.True((await ClosureTestKit.DayAsync(first)).CanClose);
        var closure = await ClosureTestKit.CloseDayOkAsync(first);
        Assert.Equal(kit.Today(), closure.BusinessDate);
        Assert.Equal(2, closure.Report.Sessions.Count);
        // Un magasin voisin n'est pas concerné : ses opérations restent sans journée.
        Assert.Equal(0, await CountClosuresAsync(server.OtherStoreId));
    }

    [Fact]
    public async Task La_cloture_fige_la_journee_comptable_sur_toutes_les_operations()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var customer = await kit.Sales.CreateCustomerAsync();
        var cash = await kit.SellAsync(poste, store, price: 300m, quantity: 2);
        var card = await kit.SellAsync(poste, store, price: 450m, mode: SalePaymentModes.Card);
        (await SalesTestKit.CardResultAsync(poste, card.Id, accepted: true)).EnsureSuccessStatusCode();
        var refused = await kit.SellAsync(poste, store, price: 700m, mode: SalePaymentModes.Card);
        (await SalesTestKit.CardResultAsync(poste, refused.Id, accepted: false)).EnsureSuccessStatusCode();
        var debt = await kit.SellAsync(poste, store, price: 800m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);
        (await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 800m, null, debt.Receivable!.Id)).EnsureSuccessStatusCode();
        (await poste.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.Out, 50m, "Courses")))
            .EnsureSuccessStatusCode();
        var cancelled = await kit.SellAsync(poste, store, price: 120m);
        (await SalesTestKit.CancelAsync(poste, cancelled.Id)).EnsureSuccessStatusCode();
        await kit.ReturnOkAsync(poste, cash, Resellable);
        await ClosureTestKit.CloseSessionOkAsync(poste);
        var today = kit.Today();
        foreach (var table in ClosureTestKit.DayTables)
        {
            Assert.True(await kit.CountDayRowsAsync(table, store.Id, null) > 0, $"Aucune ligne à figer dans {table}.");
            Assert.Equal(0, await kit.CountDayRowsAsync(table, store.Id, today));
        }

        var closure = await ClosureTestKit.CloseDayOkAsync(poste);

        Assert.Equal((store.Id, today, false, server.AdminId), (closure.StoreId, closure.BusinessDate, closure.Forced, closure.ClosedById));
        // Ventes, paiements, règlements, mouvements de caisse, annulations, retours, sessions : tout porte la journée.
        foreach (var table in ClosureTestKit.DayTables)
        {
            Assert.Equal(0, await kit.CountDayRowsAsync(table, store.Id, null));
            Assert.True(await kit.CountDayRowsAsync(table, store.Id, today) > 0, $"Rien de figé dans {table}.");
        }

        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ClosureAuditActions.DayClosed, closure.Id.ToString()));

        // Consultation : détail et liste des clôtures passées.
        var detail = await SalesTestKit.ReadAsync<DailyClosureDetail>(await poste.GetAsync(ClosureRoutes.Closure(closure.Id)));
        Assert.Equal(JsonSerializer.Serialize(closure), JsonSerializer.Serialize(detail));
        var listed = Assert.Single((await poste.GetFromJsonAsync<List<DailyClosureSummary>>(ClosureRoutes.Closures))!);
        Assert.Equal(
            (closure.Id, today, closure.Report.Revenue.NetRevenue, closure.Report.Treasury.NetCash, closure.Report.Treasury.NetCard),
            (listed.Id, listed.BusinessDate, listed.NetRevenue, listed.NetCash, listed.NetCard));
        Assert.Empty((await poste.GetFromJsonAsync<List<DailyClosureSummary>>(
            $"{ClosureRoutes.Closures}?du={today.AddDays(1):yyyy-MM-dd}"))!);
        // Une clôture d'un autre magasin est introuvable depuis ce poste.
        await StockTestKit.AssertErrorAsync(
            await server.AdminClient.GetAsync(ClosureRoutes.Closure(closure.Id)), HttpStatusCode.NotFound, ApiErrorCodes.NotFound);

        // La journée suivante commence vide.
        var day = await ClosureTestKit.DayAsync(poste);
        Assert.Equal((today, false), (day.LastClosedDate, day.CanClose));
        Assert.Empty(day.Preview.Sessions);
        Assert.Equal((0, 0m, 0m), (day.Preview.Revenue.SalesCount, day.Preview.Revenue.SalesTotal, day.Preview.Treasury.NetCash));
    }

    [Fact]
    public async Task Une_seule_cloture_par_magasin_et_par_jour_meme_demandee_deux_fois()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        await kit.SellAsync(poste, store, price: 990m);
        await ClosureTestKit.CloseSessionOkAsync(poste);
        var operationId = Guid.NewGuid();

        var closure = await SalesTestKit.ReadAsync<DailyClosureDetail>(await ClosureTestKit.CloseDayAsync(poste, operationId: operationId));
        // Même demande rejouée : même clôture, rien de plus.
        var replayed = await SalesTestKit.ReadAsync<DailyClosureDetail>(await ClosureTestKit.CloseDayAsync(poste, operationId: operationId));
        Assert.Equal(JsonSerializer.Serialize(closure), JsonSerializer.Serialize(replayed));
        // Nouvelle demande pour la même date : refusée.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(poste), HttpStatusCode.Conflict, ClosureErrorCodes.DayAlreadyClosed);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(poste, kit.Today().AddDays(-1)), HttpStatusCode.Conflict, ClosureErrorCodes.DayAlreadyClosed);
        // Une date future n'est pas clôturable.
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(poste, kit.Today().AddDays(1)), HttpStatusCode.BadRequest, ClosureErrorCodes.InvalidDate);

        Assert.Equal(1, await CountClosuresAsync(store.Id));

        // La base elle-même refuse une seconde clôture du même magasin pour la même date.
        await using var connection = await server.DataSource.OpenConnectionAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            WITH op AS (INSERT INTO operations (operation_id, kind, employee_id, request_hash)
                        VALUES (gen_random_uuid(), 'ESSAI', @AdminId, 'x') RETURNING operation_id)
            INSERT INTO daily_closures (store_id, business_date, closed_by, register_account_id, operation_id, report)
            SELECT dc.store_id, dc.business_date, dc.closed_by, dc.register_account_id, op.operation_id, dc.report
            FROM daily_closures dc, op WHERE dc.id = @Id
            """,
            new { closure.Id, server.AdminId }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(1, await CountClosuresAsync(store.Id));
    }

    [Fact]
    public async Task Des_clotures_simultanees_du_meme_magasin_n_en_enregistrent_qu_une()
    {
        for (var round = 0; round < 3; round++)
        {
            var store = await kit.CreateStoreAsync();
            var first = await kit.CreateRegisterClientAsync(store, 1000m);
            var second = await kit.CreateRegisterClientAsync(store, 1000m);
            var sale = await kit.SellAsync(first, store, price: 480m);
            await kit.SellAsync(second, store, price: 520m);
            await ClosureTestKit.CloseSessionOkAsync(first);
            await ClosureTestKit.CloseSessionOkAsync(second);

            var results = await SalesTestKit.InParallelAsync(
                8, index => ClosureTestKit.CloseDayAsync(index % 2 == 0 ? first : second));

            Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));
            foreach (var refused in results.Where(response => response.StatusCode != HttpStatusCode.OK))
            {
                await StockTestKit.AssertErrorAsync(refused, HttpStatusCode.Conflict, ClosureErrorCodes.DayAlreadyClosed);
            }

            Assert.Equal(1, await CountClosuresAsync(store.Id));
            Assert.Equal(AsDate(kit.Today()), await BusinessDateAsync("sales", "id", sale.Id));
            var closure = await SalesTestKit.ReadAsync<DailyClosureDetail>(await first.GetAsync(
                ClosureRoutes.Closure((await first.GetFromJsonAsync<List<DailyClosureSummary>>(ClosureRoutes.Closures))!.Single().Id)));
            Assert.Equal(1000m, closure.Report.Revenue.SalesTotal);
        }
    }

    [Fact]
    public async Task Une_cloture_et_des_annulations_simultanees_laissent_chaque_vente_dans_une_seule_journee()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 0m);
        var customer = await kit.Sales.CreateCustomerAsync();
        var sales = new List<SaleDetail>();
        for (var index = 0; index < 6; index++)
        {
            sales.Add(await kit.SellAsync(poste, store, price: 100m + index, mode: SalePaymentModes.Unpaid, customerId: customer.Id));
        }

        await ClosureTestKit.CloseSessionOkAsync(poste);

        // Une vente impayée s'annule sans caisse ouverte : six annulations partent en même temps que la clôture.
        var results = await SalesTestKit.InParallelAsync(
            7, index => index == 3 ? ClosureTestKit.CloseDayAsync(poste) : SalesTestKit.CancelAsync(poste, sales[index > 3 ? index - 1 : index].Id));

        Assert.Equal(HttpStatusCode.OK, results[3].StatusCode);
        var closure = await SalesTestKit.ReadAsync<DailyClosureDetail>(results[3]);
        var cancelledBefore = 0;
        for (var index = 0; index < results.Length; index++)
        {
            if (index == 3)
            {
                continue;
            }

            // Passée avant la clôture : annulée, dans la journée. Passée après : refusée (D13).
            if (results[index].StatusCode == HttpStatusCode.OK)
            {
                cancelledBefore++;
            }
            else
            {
                await StockTestKit.AssertErrorAsync(results[index], HttpStatusCode.Conflict, SaleErrorCodes.DayClosed);
            }
        }

        Assert.Equal(cancelledBefore, closure.Report.Revenue.CancelledCount);
        Assert.Equal(6 - cancelledBefore, closure.Report.Revenue.SalesCount);
        Assert.Equal(6 - cancelledBefore, closure.Report.Receivables.OutstandingCount);
        Assert.Equal(0, await kit.CountDayRowsAsync("sales", store.Id, null));
        Assert.Equal(0, await kit.CountDayRowsAsync("sale_cancellations", store.Id, null));
        Assert.Equal(cancelledBefore, await kit.CountDayRowsAsync("sale_cancellations", store.Id, kit.Today()));
    }

    [Fact]
    public async Task Une_operation_saisie_apres_la_cloture_appartient_a_la_journee_suivante()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var firstDay = kit.Today();
        var before = await kit.SellAsync(poste, store, price: 1100m);
        await ClosureTestKit.CloseSessionOkAsync(poste);
        var firstClosure = await ClosureTestKit.CloseDayOkAsync(poste);

        // Même jour au calendrier, après la clôture : la caisse rouvre et vend.
        var session = await ClosureTestKit.OpenAsync(poste, 300m);
        var after = await kit.SellAsync(poste, store, price: 2200m);
        (await poste.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.In, 40m, "Monnaie")))
            .EnsureSuccessStatusCode();

        // Ces opérations n'ont pas rejoint la journée clôturée…
        Assert.Null(await BusinessDateAsync("sales", "id", after.Id));
        Assert.Null(await BusinessDateAsync("payments", "sale_id", after.Id));
        Assert.Null(await BusinessDateAsync("cash_sessions", "id", session.Id));
        Assert.Equal(AsDate(firstDay), await BusinessDateAsync("sales", "id", before.Id));
        var day = await ClosureTestKit.DayAsync(poste);
        Assert.Equal((2200m, 1, firstDay), (day.Preview.Revenue.SalesTotal, day.Preview.Revenue.SalesCount, day.LastClosedDate));
        // … et le rapport figé de la veille ne les contient pas.
        Assert.Equal(1100m, (await poste.GetFromJsonAsync<DailyClosureDetail>(ClosureRoutes.Closure(firstClosure.Id)))!.Report.Revenue.SalesTotal);

        // La date déjà clôturée ne peut pas l'être une seconde fois, même la caisse fermée.
        await ClosureTestKit.CloseSessionOkAsync(poste);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(poste), HttpStatusCode.Conflict, ClosureErrorCodes.DayAlreadyClosed);
        Assert.Null(await BusinessDateAsync("sales", "id", after.Id));

        // Le lendemain, elles forment la journée suivante.
        kit.NextDay();
        var secondDay = kit.Today();
        Assert.Equal(firstDay.AddDays(1), secondDay);
        Assert.True((await ClosureTestKit.DayAsync(poste)).CanClose);
        var secondClosure = await ClosureTestKit.CloseDayOkAsync(poste);

        Assert.Equal(secondDay, secondClosure.BusinessDate);
        Assert.Equal((2200m, 2240m, 300m), (secondClosure.Report.Revenue.SalesTotal, secondClosure.Report.Treasury.NetCash, secondClosure.Report.Treasury.OpeningFloats));
        Assert.Equal(AsDate(secondDay), await BusinessDateAsync("sales", "id", after.Id));
        Assert.Equal(AsDate(secondDay), await BusinessDateAsync("payments", "sale_id", after.Id));
        Assert.Equal(AsDate(secondDay), await BusinessDateAsync("cash_sessions", "id", session.Id));
        // La première journée n'a pas bougé.
        Assert.Equal(AsDate(firstDay), await BusinessDateAsync("sales", "id", before.Id));
        Assert.Equal(2, await CountClosuresAsync(store.Id));
    }

    [Fact]
    public async Task Une_journee_oubliee_se_cloture_a_sa_date_tant_qu_aucune_cloture_plus_recente_n_existe()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 0m);
        var yesterday = kit.Today();
        var sale = await kit.SellAsync(poste, store, price: 640m);
        await ClosureTestKit.CloseSessionOkAsync(poste);

        // Personne n'a clôturé hier soir : on clôture ce matin, à la date d'hier.
        kit.NextDay();
        var closure = await SalesTestKit.ReadAsync<DailyClosureDetail>(await ClosureTestKit.CloseDayAsync(poste, yesterday));

        Assert.Equal(yesterday, closure.BusinessDate);
        Assert.Equal(AsDate(yesterday), await BusinessDateAsync("sales", "id", sale.Id));
        // La journée d'aujourd'hui reste clôturable ce soir.
        var day = await ClosureTestKit.DayAsync(poste);
        Assert.Equal((yesterday, yesterday.AddDays(1), true), (day.LastClosedDate, day.Today, day.CanClose));
        Assert.Equal(yesterday.AddDays(1), (await ClosureTestKit.CloseDayOkAsync(poste)).BusinessDate);
    }

    [Fact]
    public void La_date_du_jour_d_un_magasin_est_celle_de_l_heure_d_Algerie()
    {
        var clock = new AdjustableClock();
        var storeClock = new StoreClock(clock);
        void SetUtc(DateTimeOffset utc) => clock.Offset += utc - clock.GetUtcNow();

        // 22 h 59 UTC = 23 h 59 à Alger : encore le 30. 23 h 00 UTC = minuit à Alger : déjà le 1er.
        SetUtc(new DateTimeOffset(2026, 6, 30, 22, 59, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 6, 30), storeClock.Today());
        SetUtc(new DateTimeOffset(2026, 6, 30, 23, 0, 30, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 7, 1), storeClock.Today());
        // Pas de changement d'heure : même décalage en hiver.
        SetUtc(new DateTimeOffset(2026, 12, 31, 23, 0, 30, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2027, 1, 1), storeClock.Today());
    }

    [Fact]
    public async Task Une_vente_d_une_journee_cloturee_ne_peut_plus_etre_annulee()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var customer = await kit.Sales.CreateCustomerAsync();
        var cash = await kit.SellAsync(poste, store, price: 900m, quantity: 2);
        var unpaid = await kit.SellAsync(poste, store, price: 1300m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);
        await ClosureTestKit.CloseSessionOkAsync(poste);
        var closure = await ClosureTestKit.CloseDayOkAsync(poste);
        await ClosureTestKit.OpenAsync(poste, 5000m);

        // D13 : au-delà de la clôture, plus d'annulation — vente payée comme vente impayée.
        var error = await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(poste, cash.Id), HttpStatusCode.Conflict, SaleErrorCodes.DayClosed);
        Assert.Contains("retour", error.Message);
        await StockTestKit.AssertErrorAsync(
            await SalesTestKit.CancelAsync(poste, unpaid.Id), HttpStatusCode.Conflict, SaleErrorCodes.DayClosed);

        Assert.Equal(SaleStatuses.Completed, (await poste.GetFromJsonAsync<SaleDetail>(SaleRoutes.Sale(cash.Id)))!.Status);
        Assert.Equal(98m, await kit.Sales.Stock.ReadLevelAsync(cash.Lines[0].ProductId, store.MainLocationId));
        Assert.Equal(5000m, (await kit.Sales.SummaryAsync(poste)).ExpectedCash);
        Assert.Equal(0, await kit.Sales.ScalarAsync<long>("SELECT count(*) FROM sale_cancellations WHERE store_id = @Id", new { store.Id }));

        // On passe par un retour : il appartient à la journée où il est fait, pas à celle de la vente.
        var created = await kit.ReturnOkAsync(poste, cash, Resellable);
        Assert.Equal(900m, created.RefundAmount);
        Assert.Null(await BusinessDateAsync("returns", "id", created.Id));
        Assert.Equal(AsDate(closure.BusinessDate), await BusinessDateAsync("sales", "id", cash.Id));
        var day = await ClosureTestKit.DayAsync(poste);
        Assert.Equal((0m, 1, 900m, -900m), (day.Preview.Revenue.SalesTotal, day.Preview.Revenue.ReturnsCount, day.Preview.Revenue.ReturnsTotal, day.Preview.Revenue.NetRevenue));
        Assert.Equal(-900m, day.Preview.Treasury.NetCash);
    }

    // ---------- Rapport ----------

    [Fact]
    public async Task Le_rapport_de_cloture_distingue_chiffre_d_affaires_tresorerie_et_creances()
    {
        var store = await kit.CreateStoreAsync();
        var (clientX, clientY, clientZ) = (await kit.Sales.CreateCustomerAsync(), await kit.Sales.CreateCustomerAsync(), await kit.Sales.CreateCustomerAsync());
        var registerA = await kit.CreateRegisterAsync(store);
        var registerB = await kit.CreateRegisterAsync(store);
        var a = await kit.Sales.AdminOnRegisterAsync(registerA.Login, registerA.Password);
        var b = await kit.Sales.AdminOnRegisterAsync(registerB.Login, registerB.Password);

        // ----- Veille -----
        await ClosureTestKit.OpenAsync(a, 1000m);
        var oldDebt = await kit.SellAsync(a, store, price: 3500m, mode: SalePaymentModes.Unpaid, customerId: clientX.Id);
        var oldCash = await kit.SellAsync(a, store, price: 500m, quantity: 2);
        await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(a, 2000m));
        var eve = await ClosureTestKit.CloseDayOkAsync(a);

        // Veille : 4 500 de chiffre d'affaires, 1 000 seulement de trésorerie, 3 500 de créance.
        Assert.Equal(
            new ClosureRevenue(2, 4500m, 0m, 1000m, 0m, 3500m, 4500m, 0, 0m, 0, 0m, 0, 0m, 4500m), eve.Report.Revenue);
        Assert.Equal(
            new ClosureTreasury(1000m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 1000m, 0m, 1000m, 2000m, 2000m, 0m), eve.Report.Treasury);
        Assert.Equal(new ClosureReceivables(1, 3500m, 0, 0m, 1, 3500m), eve.Report.Receivables);

        // ----- Jour J -----
        kit.NextDay();
        var today = kit.Today();
        await ClosureTestKit.OpenAsync(a, 2000m);
        await ClosureTestKit.OpenAsync(b, 500m);
        // A : 2 × 1 200 avec 10 % de remise, en espèces → 2 160.
        await kit.SellAsync(a, store, price: 1200m, quantity: 2, discountKind: DiscountKinds.Percent, discountValue: 10m);
        // A : 800 par carte, accepté.
        var card = await kit.SellAsync(a, store, price: 800m, mode: SalePaymentModes.Card);
        (await SalesTestKit.CardResultAsync(a, card.Id, accepted: true)).EnsureSuccessStatusCode();
        // B : vente impayée de 6 200 → chiffre d'affaires SANS trésorerie.
        await kit.SellAsync(b, store, price: 6200m, mode: SalePaymentModes.Unpaid, customerId: clientY.Id);
        // B : règlement en espèces de la créance de la veille (3 500) → trésorerie SANS chiffre d'affaires.
        (await SalesTestKit.SettleAsync(b, PaymentMethods.Cash, 3500m, null, oldDebt.Receivable!.Id)).EnsureSuccessStatusCode();
        // A : vente impayée de 1 800, réglée le jour même par carte à la caisse B.
        var sameDay = await kit.SellAsync(a, store, price: 1800m, mode: SalePaymentModes.Unpaid, customerId: clientZ.Id);
        var cardSettlement = await SalesTestKit.ReadAsync<SettlementResult>(
            await SalesTestKit.SettleAsync(b, PaymentMethods.Card, 1800m, null, sameDay.Receivable!.Id));
        (await SalesTestKit.SettlementCardResultAsync(b, cardSettlement.Id, accepted: true)).EnsureSuccessStatusCode();
        // A : vente de 500 en espèces, annulée (remboursée 500).
        var cancelled = await kit.SellAsync(a, store, price: 500m);
        (await SalesTestKit.CancelAsync(a, cancelled.Id)).EnsureSuccessStatusCode();
        // B : vente de 900 par carte, refusée par le TPE.
        var refused = await kit.SellAsync(b, store, price: 900m, mode: SalePaymentModes.Card);
        (await SalesTestKit.CardResultAsync(b, refused.Id, accepted: false)).EnsureSuccessStatusCode();
        // A : entrée de 300 ; B : sortie de 150.
        (await a.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.In, 300m, "Monnaie")))
            .EnsureSuccessStatusCode();
        (await b.PostAsJsonAsync(CashRoutes.Movements, new CashMovementRequest(Guid.NewGuid(), CashMovementKinds.Out, 150m, "Achat de sacs")))
            .EnsureSuccessStatusCode();
        // A : retour d'une unité (500) d'une vente de la veille, remboursée en espèces.
        await kit.ReturnOkAsync(a, oldCash, Resellable);

        var expectedRevenue = new ClosureRevenue(
            SalesCount: 4, Subtotal: 2400m + 800m + 6200m + 1800m, Discounts: 240m,
            CashSales: 2160m, CardSales: 800m, UnpaidSales: 8000m, SalesTotal: 10_960m,
            CancelledCount: 1, CancelledTotal: 500m, CardRefusedCount: 1, CardRefusedTotal: 900m,
            ReturnsCount: 1, ReturnsTotal: 500m, NetRevenue: 10_460m);
        var expectedReceivables = new ClosureReceivables(
            CreatedCount: 2, CreatedAmount: 8000m, SettledCount: 2, SettledAmount: 5300m, OutstandingCount: 1, OutstandingAmount: 6200m);

        // Aperçu avant clôture : mêmes totaux, caisses encore ouvertes, rien de compté.
        var preview = (await ClosureTestKit.DayAsync(a)).Preview;
        Assert.Equal(expectedRevenue, preview.Revenue);
        Assert.Equal(expectedReceivables, preview.Receivables);
        Assert.Equal((5310m, 2600m, 7810m, 0m), (preview.Treasury.NetCash, preview.Treasury.NetCard, preview.Treasury.ExpectedCash, preview.Treasury.CountedCash));
        Assert.All(preview.Sessions, session => Assert.Null(session.CountedCash));

        // Comptage par caisse : A juste (3 960), B avec 10 de moins (3 840 pour 3 850 attendus).
        var closedA = await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(a, 3960m));
        var closedB = await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(b, 3840m));
        Assert.Equal((3960m, 0m), (closedA.ExpectedCash, closedA.CashDifference));
        Assert.Equal((3850m, -10m), (closedB.ExpectedCash, closedB.CashDifference));

        var closure = await ClosureTestKit.CloseDayOkAsync(b);
        var report = closure.Report;

        Assert.Equal(today, closure.BusinessDate);
        Assert.Equal(expectedRevenue, report.Revenue);
        Assert.Equal(
            new ClosureTreasury(
                SaleCash: 2160m + 500m, SaleCard: 800m,
                OldReceivablesCash: 3500m, OldReceivablesCard: 0m,
                SameDayReceivablesCash: 0m, SameDayReceivablesCard: 1800m,
                CancellationCashRefunds: 500m, CancellationCardRefunds: 0m,
                ReturnCashRefunds: 500m, ReturnCardRefunds: 0m,
                CashIn: 300m, CashOut: 150m,
                NetCash: 5310m, NetCard: 2600m,
                OpeningFloats: 2500m, ExpectedCash: 7810m, CountedCash: 7800m, CashDifference: -10m),
            report.Treasury);
        Assert.Equal(expectedReceivables, report.Receivables);
        Assert.Equal((false, 0, (string?)null), (report.ForcedWithUnknownOperations, report.UnknownOfflineOperations, report.Warning));

        // Chiffre d'affaires ≠ trésorerie : 10 460 vendus, 7 910 encaissés.
        Assert.Equal(10_460m, report.Revenue.NetRevenue);
        Assert.Equal(7910m, report.Treasury.NetCash + report.Treasury.NetCard);
        Assert.NotEqual(report.Revenue.NetRevenue, report.Treasury.NetCash + report.Treasury.NetCard);
        // La vente impayée du jour est dans le chiffre d'affaires, dans aucun encaissement.
        Assert.Equal(8000m, report.Revenue.UnpaidSales);
        Assert.Equal(2160m + 800m, report.Revenue.CashSales + report.Revenue.CardSales);
        // Le règlement de la créance de la veille est dans la trésorerie, pas dans le chiffre d'affaires du jour.
        Assert.Equal(3500m, report.Treasury.OldReceivablesCash);
        Assert.Equal(report.Revenue.SalesTotal, report.Revenue.Subtotal - report.Revenue.Discounts);

        // Par caisse.
        Assert.Equal(2, report.Sessions.Count);
        var sessionA = report.Sessions.Single(session => session.Id == closedA.Id);
        var sessionB = report.Sessions.Single(session => session.Id == closedB.Id);
        Assert.Equal(JsonSerializer.Serialize(closedA), JsonSerializer.Serialize(sessionA));
        Assert.Equal(JsonSerializer.Serialize(closedB), JsonSerializer.Serialize(sessionB));
        Assert.Equal(
            (2000m, 2660m, 800m, 0m, 0m, 300m, 0m, 500m, 500m, 3960m, 3960m, 0m),
            (sessionA.OpeningFloat, sessionA.SaleCash, sessionA.SaleCard, sessionA.SettlementCash, sessionA.SettlementCard,
             sessionA.CashIn, sessionA.CashOut, sessionA.CancellationCashRefunds, sessionA.ReturnCashRefunds,
             sessionA.ExpectedCash, sessionA.CountedCash, sessionA.CashDifference));
        Assert.Equal(
            (500m, 0m, 0m, 3500m, 1800m, 0m, 150m, 0m, 0m, 3850m, 3840m, -10m),
            (sessionB.OpeningFloat, sessionB.SaleCash, sessionB.SaleCard, sessionB.SettlementCash, sessionB.SettlementCard,
             sessionB.CashIn, sessionB.CashOut, sessionB.CancellationCashRefunds, sessionB.ReturnCashRefunds,
             sessionB.ExpectedCash, sessionB.CountedCash, sessionB.CashDifference));

        // Le rapport de la veille n'a pas été réécrit par les opérations du jour.
        Assert.Equal(
            JsonSerializer.Serialize(eve),
            JsonSerializer.Serialize(await a.GetFromJsonAsync<DailyClosureDetail>(ClosureRoutes.Closure(eve.Id))));
        var listed = (await a.GetFromJsonAsync<List<DailyClosureSummary>>(ClosureRoutes.Closures))!;
        Assert.Equal(new[] { today, today.AddDays(-1) }, listed.Select(line => line.BusinessDate));
        Assert.Equal((10_460m, 5310m, 2600m, -10m), (listed[0].NetRevenue, listed[0].NetCash, listed[0].NetCard, listed[0].CashDifference));
    }

    [Fact]
    public async Task Un_rapport_de_cloture_est_fige_et_ne_peut_plus_etre_modifie()
    {
        var store = await kit.CreateStoreAsync();
        var poste = await kit.CreateRegisterClientAsync(store, 1000m);
        var customer = await kit.Sales.CreateCustomerAsync();
        var sale = await kit.SellAsync(poste, store, price: 750m, quantity: 2);
        var debt = await kit.SellAsync(poste, store, price: 400m, mode: SalePaymentModes.Unpaid, customerId: customer.Id);
        var session = (await poste.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session!;
        await ClosureTestKit.CloseSessionOkAsync(poste, difference: 5m);
        var closure = await ClosureTestKit.CloseDayOkAsync(poste);
        var frozen = JsonSerializer.Serialize(closure);
        Assert.Equal((1900m, 1500m, 5m, 400m), (closure.Report.Revenue.SalesTotal, closure.Report.Treasury.NetCash, closure.Report.Treasury.CashDifference, closure.Report.Receivables.OutstandingAmount));

        // Le lendemain : retour sur une vente clôturée, règlement de sa créance, nouvelles ventes.
        kit.NextDay();
        await ClosureTestKit.OpenAsync(poste, 2000m);
        await kit.ReturnOkAsync(poste, sale, Resellable);
        (await SalesTestKit.SettleAsync(poste, PaymentMethods.Cash, 400m, null, debt.Receivable!.Id)).EnsureSuccessStatusCode();
        await kit.SellAsync(poste, store, price: 60m);

        // Le rapport stocké est rendu tel quel : il n'est pas recalculé.
        Assert.Equal(frozen, JsonSerializer.Serialize(await poste.GetFromJsonAsync<DailyClosureDetail>(ClosureRoutes.Closure(closure.Id))));

        // La base refuse toute modification de la clôture, du rapport, et de la journée des opérations figées.
        var attempts = new (string Sql, object Parameters)[]
        {
            ("UPDATE daily_closures SET report = '{}'::jsonb WHERE id = @Id", new { closure.Id }),
            ("UPDATE daily_closures SET business_date = business_date + 30 WHERE id = @Id", new { closure.Id }),
            ("DELETE FROM daily_closures WHERE id = @Id", new { closure.Id }),
            ("UPDATE sales SET business_date = NULL WHERE id = @Id", new { sale.Id }),
            ("UPDATE sales SET business_date = business_date + 1 WHERE id = @Id", new { sale.Id }),
            ("UPDATE payments SET business_date = NULL WHERE sale_id = @Id", new { sale.Id }),
            ("UPDATE cash_sessions SET business_date = NULL WHERE id = @Id", new { session.Id }),
            ("UPDATE cash_sessions SET counted_cash = counted_cash + 5, cash_difference = cash_difference + 5 WHERE id = @Id", new { session.Id }),
            ("UPDATE cash_sessions SET status = 'OUVERTE', closed_at = NULL, closed_by = NULL, counted_cash = NULL, expected_cash = NULL, cash_difference = NULL, close_operation_id = NULL, business_date = NULL WHERE id = @Id", new { session.Id }),
            ("DELETE FROM cash_sessions WHERE id = @Id", new { session.Id }),
        };
        foreach (var (sql, parameters) in attempts)
        {
            await using var connection = await server.DataSource.OpenConnectionAsync();
            var exception = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql, parameters));
            Assert.True(
                exception.SqlState is PostgresErrorCodes.IntegrityConstraintViolation or PostgresErrorCodes.ForeignKeyViolation,
                $"{sql} → {exception.SqlState} {exception.MessageText}");
        }

        Assert.Equal(frozen, JsonSerializer.Serialize(await poste.GetFromJsonAsync<DailyClosureDetail>(ClosureRoutes.Closure(closure.Id))));

        // Les opérations du lendemain forment bien une autre journée, avec son propre rapport.
        await ClosureTestKit.CloseSessionOkAsync(poste);
        var next = await ClosureTestKit.CloseDayOkAsync(poste);
        Assert.Equal(
            (60m, 750m, -690m, 400m, 0m),
            (next.Report.Revenue.SalesTotal, next.Report.Revenue.ReturnsTotal, next.Report.Revenue.NetRevenue,
             next.Report.Treasury.OldReceivablesCash, next.Report.Receivables.OutstandingAmount));
        Assert.Equal(60m + 400m - 750m, next.Report.Treasury.NetCash);
    }

    // ---------- Opérations de secours non synchronisées ----------

    [Fact]
    public async Task La_cloture_est_refusee_tant_que_des_operations_de_secours_ne_sont_pas_synchronisees()
    {
        var store = await kit.CreateStoreAsync();
        var register = await kit.CreateRegisterAsync(store);
        var poste = await kit.Sales.AdminOnRegisterAsync(register.Login, register.Password);
        await ClosureTestKit.OpenAsync(poste, 1000m);
        var sale = await kit.SellAsync(poste, store, price: 1500m);
        await ClosureTestKit.CloseSessionOkAsync(poste);
        // Le mode secours n'existe pas encore : la ligne est insérée à la main.
        await kit.Sales.ScalarAsync<int>(
            "INSERT INTO offline_pending_operations (store_id, description) VALUES (@Id, 'Vente de secours non reçue') RETURNING 1", new { store.Id });

        // Clôture normale : refusée, rien n'est figé.
        var error = await AssertBlockedAsync(await ClosureTestKit.CloseDayAsync(poste), ClosureErrorCodes.OfflineOperationsPending);
        Assert.Equal(ClosureBlockerKinds.OfflineOperations, Assert.Single(error.Blockers).Kind);
        var day = await ClosureTestKit.DayAsync(poste);
        Assert.Equal((false, 1), (day.CanClose, day.PendingOfflineOperations));
        Assert.Equal(ClosureBlockerKinds.OfflineOperations, Assert.Single(day.Blockers).Kind);
        Assert.Equal(0, await CountClosuresAsync(store.Id));
        Assert.Equal(0, await CountStampedAsync(store.Id));

        // Forçage sans motif : refusé.
        foreach (var reason in new[] { null, "   " })
        {
            await StockTestKit.AssertErrorAsync(
                await ClosureTestKit.CloseDayAsync(poste, force: true, forceReason: reason), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        }

        // Forçage par un employé qui a seulement « clôturer une caisse » : refusé et audité.
        var closer = await kit.EmployeeOnRegisterAsync(register, Permissions.RegisterClose, Permissions.RegisterReportsView);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(closer, force: true, forceReason: "Serveur de secours perdu"),
            HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await AssertBlockedAsync(await ClosureTestKit.CloseDayAsync(closer), ClosureErrorCodes.OfflineOperationsPending);
        Assert.Equal(0, await CountClosuresAsync(store.Id));
        Assert.Null(await BusinessDateAsync("sales", "id", sale.Id));

        // Procédure exceptionnelle explicite : motif obligatoire, auditée, marquée dans le rapport.
        var forced = await SalesTestKit.ReadAsync<DailyClosureDetail>(
            await ClosureTestKit.CloseDayAsync(poste, force: true, forceReason: " Poste de secours volé, opérations irrécupérables "));

        Assert.True(forced.Forced);
        Assert.Equal("Poste de secours volé, opérations irrécupérables", forced.ForceReason);
        Assert.Equal(
            (true, 1, "Clôture forcée avec opérations inconnues"),
            (forced.Report.ForcedWithUnknownOperations, forced.Report.UnknownOfflineOperations, forced.Report.Warning));
        Assert.Equal(1500m, forced.Report.Revenue.SalesTotal);
        Assert.Equal(AsDate(kit.Today()), await BusinessDateAsync("sales", "id", sale.Id));
        Assert.Equal(1, await kit.Sales.Stock.CountAuditAsync(ClosureAuditActions.DayClosedForced, forced.Id.ToString()));
        Assert.Equal(0, await kit.Sales.Stock.CountAuditAsync(ClosureAuditActions.DayClosed, forced.Id.ToString()));
        Assert.True(Assert.Single((await poste.GetFromJsonAsync<List<DailyClosureSummary>>(ClosureRoutes.Closures))!).Forced);
        Assert.Equal(
            "true|1|Poste de secours volé, opérations irrécupérables",
            await kit.Sales.ScalarAsync<string>(
                "SELECT forced || '|' || unknown_offline_operations || '|' || force_reason FROM daily_closures WHERE id = @Id", new { forced.Id }));

        // Forcer sans opération en attente n'a pas de sens : refusé, la clôture normale suffit.
        var quiet = await kit.CreateStoreAsync();
        var quietPoste = await kit.CreateRegisterClientAsync(quiet, openingFloat: null);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(quietPoste, force: true, forceReason: "Par habitude"), HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest);
        Assert.False((await ClosureTestKit.CloseDayOkAsync(quietPoste)).Forced);

        // Une opération synchronisée ne bloque plus.
        var synced = await kit.CreateStoreAsync();
        var syncedPoste = await kit.CreateRegisterClientAsync(synced, openingFloat: null);
        await kit.Sales.ScalarAsync<int>(
            "INSERT INTO offline_pending_operations (store_id, synced_at) VALUES (@Id, now()) RETURNING 1", new { synced.Id });
        Assert.False((await ClosureTestKit.CloseDayOkAsync(syncedPoste)).Forced);
    }

    // ---------- Droits ----------

    [Fact]
    public async Task La_fermeture_la_cloture_et_les_rapports_sont_reserves_aux_employes_habilites()
    {
        var store = await kit.CreateStoreAsync();
        var register = await kit.CreateRegisterAsync(store);
        var admin = await kit.Sales.AdminOnRegisterAsync(register.Login, register.Password);
        await ClosureTestKit.OpenAsync(admin, 1000m);
        await kit.SellAsync(admin, store, price: 100m);

        // Ouvrir une caisse, vendre, encaisser, consulter les rapports : rien de cela ne permet de fermer ou de clôturer.
        var cashier = await kit.EmployeeOnRegisterAsync(
            register, Permissions.RegisterOpen, Permissions.SalesCreate, Permissions.PaymentsCollect, Permissions.RegisterReportsView);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseSessionAsync(cashier, 1100m), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(cashier), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        Assert.NotNull((await admin.GetFromJsonAsync<CurrentCashSessionResponse>(CashRoutes.CurrentSession))!.Session);
        // « Consulter les rapports » donne l'état de la journée et les clôtures passées.
        (await cashier.GetAsync(ClosureRoutes.CurrentDay)).EnsureSuccessStatusCode();
        (await cashier.GetAsync(ClosureRoutes.Closures)).EnsureSuccessStatusCode();

        // « Clôturer une caisse » ferme et clôture, mais ne donne pas les rapports.
        var (number, closerId) = await kit.Sales.CreateEmployeeAsync(Permissions.RegisterClose);
        var closer = await server.CreatePosteClientAsync(register.Login, register.Password);
        await ServerFixture.IdentifyAsync(closer, number, SalesTestKit.EmployeeSecret);
        await StockTestKit.AssertErrorAsync(
            await closer.GetAsync(ClosureRoutes.CurrentDay), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        await StockTestKit.AssertErrorAsync(
            await closer.GetAsync(ClosureRoutes.Closures), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);
        var closed = await SalesTestKit.ReadAsync<CashSessionReport>(await ClosureTestKit.CloseSessionAsync(closer, 1100m));
        Assert.Equal($"Employé {number}", closed.ClosedByName);
        var closure = await SalesTestKit.ReadAsync<DailyClosureDetail>(await ClosureTestKit.CloseDayAsync(closer));
        Assert.Equal(closerId, closure.ClosedById);
        await StockTestKit.AssertErrorAsync(
            await closer.GetAsync(ClosureRoutes.Closure(closure.Id)), HttpStatusCode.Forbidden, ApiErrorCodes.PermissionDenied);

        // Poste sans employé identifié.
        var poste = await server.CreatePosteClientAsync(register.Login, register.Password);
        await StockTestKit.AssertErrorAsync(
            await ClosureTestKit.CloseDayAsync(poste), HttpStatusCode.Unauthorized, ApiErrorCodes.EmployeeNotIdentified);
        Assert.Equal(1, await CountClosuresAsync(store.Id));
    }
}
