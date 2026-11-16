using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;

namespace Pharmaly.Server.Closures;

/// <summary>
/// Calcul des totaux d'une caisse et du rapport d'une journée.
/// <para>
/// « La journée en cours » d'un magasin = toutes ses opérations dont « business_date » est
/// encore vide. La clôture calcule ce rapport une fois, le stocke, puis renseigne la date :
/// l'aperçu avant clôture et le rapport figé sortent donc du même calcul.
/// </para>
/// <para>
/// Chiffre d'affaires et trésorerie ne sont jamais additionnés : une vente impayée est du
/// chiffre d'affaires sans trésorerie, le règlement d'une créance d'un autre jour est de la
/// trésorerie sans chiffre d'affaires.
/// </para>
/// </summary>
public static class ClosureReports
{
    public const string ForcedWarning = "Clôture forcée avec opérations inconnues";

    private const string SessionSql =
        """
        SELECT cs.id AS Id, cs.register_account_id AS RegisterAccountId, ra.display_name AS RegisterName, cs.status AS Status,
               eo.full_name AS OpenedByName, cs.opened_at AS OpenedAt, ec.full_name AS ClosedByName, cs.closed_at AS ClosedAt,
               cs.opening_float AS OpeningFloat, cs.counted_cash AS CountedCash,
          (SELECT COALESCE(sum(amount), 0) FROM payments
            WHERE cash_session_id = cs.id AND method = 'ESPECES' AND status IN ('CONFIRME', 'REMBOURSE')) AS SaleCash,
          (SELECT COALESCE(sum(amount), 0) FROM payments
            WHERE cash_session_id = cs.id AND method = 'TPE' AND status IN ('CONFIRME', 'REMBOURSE')) AS SaleCard,
          (SELECT COALESCE(sum(amount), 0) FROM receivable_settlements
            WHERE cash_session_id = cs.id AND method = 'ESPECES' AND status = 'CONFIRME') AS SettlementCash,
          (SELECT COALESCE(sum(amount), 0) FROM receivable_settlements
            WHERE cash_session_id = cs.id AND method = 'TPE' AND status = 'CONFIRME') AS SettlementCard,
          (SELECT COALESCE(sum(amount), 0) FROM cash_movements WHERE cash_session_id = cs.id AND kind = 'ENTREE') AS CashIn,
          (SELECT COALESCE(sum(amount), 0) FROM cash_movements WHERE cash_session_id = cs.id AND kind = 'SORTIE') AS CashOut,
          (SELECT COALESCE(sum(refund_amount), 0) FROM sale_cancellations
            WHERE cash_session_id = cs.id AND refund_method = 'ESPECES') AS CancellationCashRefunds,
          (SELECT COALESCE(sum(refund_amount), 0) FROM sale_cancellations
            WHERE cash_session_id = cs.id AND refund_method = 'TPE') AS CancellationCardRefunds,
          (SELECT COALESCE(sum(refund_amount), 0) FROM returns
            WHERE cash_session_id = cs.id AND refund_method = 'ESPECES') AS ReturnCashRefunds,
          (SELECT COALESCE(sum(refund_amount), 0) FROM returns
            WHERE cash_session_id = cs.id AND refund_method = 'TPE') AS ReturnCardRefunds,
          (SELECT count(*) FROM payments WHERE cash_session_id = cs.id AND status = 'EN_ATTENTE')
          + (SELECT count(*) FROM receivable_settlements WHERE cash_session_id = cs.id AND status = 'EN_ATTENTE') AS PendingCardCount
        FROM cash_sessions cs
        JOIN register_accounts ra ON ra.id = cs.register_account_id
        JOIN employees eo ON eo.id = cs.opened_by
        LEFT JOIN employees ec ON ec.id = cs.closed_by
        """;

    /// <summary>
    /// Espèces qui doivent se trouver dans la caisse : fond + ventes en espèces + règlements
    /// d'impayés en espèces + entrées − sorties − remboursements en espèces (annulations et retours).
    /// </summary>
    public static decimal ExpectedCash(
        decimal openingFloat, decimal saleCash, decimal settlementCash, decimal cashIn, decimal cashOut,
        decimal cancellationCashRefunds, decimal returnCashRefunds) =>
        openingFloat + saleCash + settlementCash + cashIn - cashOut - cancellationCashRefunds - returnCashRefunds;

    public static async Task<CashSessionReport> ReadSessionAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId) =>
        ToReport(await connection.QuerySingleAsync<SessionRow>(SessionSql + " WHERE cs.id = @sessionId", new { sessionId }, transaction));

    /// <summary>Caisses de la journée en cours du magasin : ouvertes, ou fermées sans journée clôturée.</summary>
    public static async Task<List<CashSessionReport>> ReadOpenDaySessionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId) =>
        (await connection.QueryAsync<SessionRow>(
            SessionSql + " WHERE cs.store_id = @storeId AND cs.business_date IS NULL ORDER BY ra.display_name, cs.opened_at, cs.id",
            new { storeId },
            transaction)).Select(ToReport).ToList();

    /// <summary>Rapport de la journée en cours du magasin (rien n'est écrit).</summary>
    public static async Task<ClosureReport> ComputeOpenDayAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId, int unknownOfflineOperations = 0, bool forced = false)
    {
        // Un seul ordre SQL : tous les totaux sont lus sur la même photographie de la base.
        var totals = await connection.QuerySingleAsync<TotalsRow>(
            """
            WITH day_sales AS (
                SELECT s.id, s.status, s.payment_mode, s.subtotal, s.discount_amount, s.total, x.kind AS cancellation_kind
                FROM sales s LEFT JOIN sale_cancellations x ON x.sale_id = s.id
                WHERE s.store_id = @storeId AND s.business_date IS NULL),
            day_payments AS (
                SELECT method, amount FROM payments
                WHERE store_id = @storeId AND business_date IS NULL AND status IN ('CONFIRME', 'REMBOURSE')),
            -- Chaque créance réglée ce jour, avec le moyen du règlement. « same_day » : la vente
            -- d'origine fait partie de la journée en cours de ce magasin.
            day_settled AS (
                SELECT st.method, i.amount,
                       (s.store_id = @storeId AND s.business_date IS NULL) AS same_day
                FROM receivable_settlements st
                JOIN receivable_settlement_items i ON i.settlement_id = st.id
                JOIN receivables r ON r.id = i.receivable_id
                JOIN sales s ON s.id = r.sale_id
                WHERE st.store_id = @storeId AND st.business_date IS NULL AND st.status = 'CONFIRME'),
            day_cancellations AS (
                SELECT refund_method, refund_amount FROM sale_cancellations
                WHERE store_id = @storeId AND business_date IS NULL),
            day_returns AS (
                SELECT refund_method, refund_amount FROM returns WHERE store_id = @storeId AND business_date IS NULL),
            day_movements AS (
                SELECT kind, amount FROM cash_movements WHERE store_id = @storeId AND business_date IS NULL)
            SELECT
              (SELECT count(*) FROM day_sales WHERE status = 'VALIDEE') AS SalesCount,
              (SELECT COALESCE(sum(subtotal), 0) FROM day_sales WHERE status = 'VALIDEE') AS Subtotal,
              (SELECT COALESCE(sum(discount_amount), 0) FROM day_sales WHERE status = 'VALIDEE') AS Discounts,
              (SELECT COALESCE(sum(total), 0) FROM day_sales WHERE status = 'VALIDEE' AND payment_mode = 'ESPECES') AS CashSales,
              (SELECT COALESCE(sum(total), 0) FROM day_sales WHERE status = 'VALIDEE' AND payment_mode = 'TPE') AS CardSales,
              (SELECT COALESCE(sum(total), 0) FROM day_sales WHERE status = 'VALIDEE' AND payment_mode = 'IMPAYE') AS UnpaidSales,
              (SELECT count(*) FROM day_sales WHERE status = 'VALIDEE' AND payment_mode = 'IMPAYE') AS UnpaidCount,
              (SELECT count(*) FROM day_sales WHERE cancellation_kind = 'ANNULATION') AS CancelledCount,
              (SELECT COALESCE(sum(total), 0) FROM day_sales WHERE cancellation_kind = 'ANNULATION') AS CancelledTotal,
              (SELECT count(*) FROM day_sales WHERE cancellation_kind = 'TPE_REFUSE') AS CardRefusedCount,
              (SELECT COALESCE(sum(total), 0) FROM day_sales WHERE cancellation_kind = 'TPE_REFUSE') AS CardRefusedTotal,
              (SELECT count(*) FROM day_returns) AS ReturnsCount,
              (SELECT COALESCE(sum(refund_amount), 0) FROM day_returns) AS ReturnsTotal,
              (SELECT COALESCE(sum(refund_amount), 0) FROM day_returns WHERE refund_method = 'ESPECES') AS ReturnCashRefunds,
              (SELECT COALESCE(sum(refund_amount), 0) FROM day_returns WHERE refund_method = 'TPE') AS ReturnCardRefunds,
              (SELECT COALESCE(sum(amount), 0) FROM day_payments WHERE method = 'ESPECES') AS SaleCash,
              (SELECT COALESCE(sum(amount), 0) FROM day_payments WHERE method = 'TPE') AS SaleCard,
              (SELECT COALESCE(sum(amount), 0) FROM day_settled WHERE method = 'ESPECES' AND NOT same_day) AS OldReceivablesCash,
              (SELECT COALESCE(sum(amount), 0) FROM day_settled WHERE method = 'TPE' AND NOT same_day) AS OldReceivablesCard,
              (SELECT COALESCE(sum(amount), 0) FROM day_settled WHERE method = 'ESPECES' AND same_day) AS SameDayReceivablesCash,
              (SELECT COALESCE(sum(amount), 0) FROM day_settled WHERE method = 'TPE' AND same_day) AS SameDayReceivablesCard,
              (SELECT count(*) FROM day_settled) AS SettledCount,
              (SELECT COALESCE(sum(amount), 0) FROM day_settled) AS SettledAmount,
              (SELECT COALESCE(sum(refund_amount), 0) FROM day_cancellations WHERE refund_method = 'ESPECES') AS CancellationCashRefunds,
              (SELECT COALESCE(sum(refund_amount), 0) FROM day_cancellations WHERE refund_method = 'TPE') AS CancellationCardRefunds,
              (SELECT COALESCE(sum(amount), 0) FROM day_movements WHERE kind = 'ENTREE') AS CashIn,
              (SELECT COALESCE(sum(amount), 0) FROM day_movements WHERE kind = 'SORTIE') AS CashOut,
              (SELECT count(*) FROM receivables WHERE store_id = @storeId AND status = 'OUVERTE') AS OutstandingCount,
              (SELECT COALESCE(sum(amount), 0) FROM receivables WHERE store_id = @storeId AND status = 'OUVERTE') AS OutstandingAmount
            """,
            new { storeId },
            transaction);
        var sessions = await ReadOpenDaySessionsAsync(connection, transaction, storeId);

        var salesTotal = totals.CashSales + totals.CardSales + totals.UnpaidSales;
        var netCash = totals.SaleCash + totals.OldReceivablesCash + totals.SameDayReceivablesCash + totals.CashIn
            - totals.CashOut - totals.CancellationCashRefunds - totals.ReturnCashRefunds;
        var netCard = totals.SaleCard + totals.OldReceivablesCard + totals.SameDayReceivablesCard
            - totals.CancellationCardRefunds - totals.ReturnCardRefunds;
        var openingFloats = sessions.Sum(session => session.OpeningFloat);

        return new ClosureReport(
            new ClosureRevenue(
                (int)totals.SalesCount, totals.Subtotal, totals.Discounts, totals.CashSales, totals.CardSales, totals.UnpaidSales,
                salesTotal, (int)totals.CancelledCount, totals.CancelledTotal, (int)totals.CardRefusedCount, totals.CardRefusedTotal,
                (int)totals.ReturnsCount, totals.ReturnsTotal, salesTotal - totals.ReturnsTotal),
            new ClosureTreasury(
                totals.SaleCash, totals.SaleCard, totals.OldReceivablesCash, totals.OldReceivablesCard,
                totals.SameDayReceivablesCash, totals.SameDayReceivablesCard,
                totals.CancellationCashRefunds, totals.CancellationCardRefunds, totals.ReturnCashRefunds, totals.ReturnCardRefunds,
                totals.CashIn, totals.CashOut, netCash, netCard, openingFloats,
                // Calculé à partir des caisses : doit valoir fonds + NetCash (vérifié par les tests).
                sessions.Sum(session => session.ExpectedCash),
                sessions.Sum(session => session.CountedCash ?? 0m),
                sessions.Sum(session => session.CashDifference ?? 0m)),
            new ClosureReceivables(
                (int)totals.UnpaidCount, totals.UnpaidSales, (int)totals.SettledCount, totals.SettledAmount,
                (int)totals.OutstandingCount, totals.OutstandingAmount),
            sessions,
            forced,
            unknownOfflineOperations,
            forced ? ForcedWarning : null);
    }

    /// <summary>
    /// Ce qui empêche de clôturer la journée du magasin (ou, avec <paramref name="sessionId"/>, de
    /// fermer cette caisse : seuls ses paiements par carte en attente comptent alors).
    /// </summary>
    public static async Task<List<ClosureBlocker>> ReadBlockersAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId, Guid? sessionId = null)
    {
        var blockers = new List<ClosureBlocker>();
        if (sessionId is null)
        {
            var open = await connection.QueryAsync<(Guid Id, string RegisterName)>(
                """
                SELECT cs.id, ra.display_name FROM cash_sessions cs
                JOIN register_accounts ra ON ra.id = cs.register_account_id
                WHERE cs.store_id = @storeId AND cs.status = 'OUVERTE'
                ORDER BY ra.display_name
                """,
                new { storeId },
                transaction);
            blockers.AddRange(open.Select(session => new ClosureBlocker(
                ClosureBlockerKinds.SessionOpen,
                $"La caisse « {session.RegisterName} » est encore ouverte : comptez ses espèces et fermez-la.",
                session.Id, session.RegisterName)));
        }

        var payments = await connection.QueryAsync<(Guid SaleId, string TicketNumber, decimal Amount, string RegisterName)>(
            """
            SELECT p.sale_id, s.ticket_number, p.amount, ra.display_name
            FROM payments p
            JOIN sales s ON s.id = p.sale_id
            JOIN register_accounts ra ON ra.id = p.register_account_id
            WHERE p.status = 'EN_ATTENTE'
              AND ((@sessionId::uuid IS NULL AND p.store_id = @storeId AND p.business_date IS NULL) OR p.cash_session_id = @sessionId)
            ORDER BY s.ticket_seq
            """,
            new { storeId, sessionId },
            transaction);
        blockers.AddRange(payments.Select(payment => new ClosureBlocker(
            ClosureBlockerKinds.SaleCardPending,
            $"Ticket {payment.TicketNumber} : paiement par carte de {Money.ToDisplay(payment.Amount)} en attente "
            + $"sur « {payment.RegisterName} ». Indiquez « accepté » ou « refusé ».",
            payment.SaleId, payment.RegisterName, payment.Amount)));

        var settlements = await connection.QueryAsync<(Guid Id, string CustomerName, decimal Amount, string RegisterName)>(
            """
            SELECT st.id, c.name, st.amount, ra.display_name
            FROM receivable_settlements st
            JOIN customers c ON c.id = st.customer_id
            JOIN register_accounts ra ON ra.id = st.register_account_id
            WHERE st.status = 'EN_ATTENTE'
              AND ((@sessionId::uuid IS NULL AND st.store_id = @storeId AND st.business_date IS NULL) OR st.cash_session_id = @sessionId)
            ORDER BY st.created_at
            """,
            new { storeId, sessionId },
            transaction);
        blockers.AddRange(settlements.Select(settlement => new ClosureBlocker(
            ClosureBlockerKinds.SettlementCardPending,
            $"Règlement d'impayés de {settlement.CustomerName} : paiement par carte de {Money.ToDisplay(settlement.Amount)} en attente "
            + $"sur « {settlement.RegisterName} ». Indiquez « accepté » ou « refusé ».",
            settlement.Id, settlement.RegisterName, settlement.Amount)));

        return blockers;
    }

    public static ClosureBlocker OfflineBlocker(int count) =>
        new(
            ClosureBlockerKinds.OfflineOperations,
            $"{count} opération(s) de secours en attente de synchronisation : la journée ne peut pas être présentée comme consolidée.");

    private static CashSessionReport ToReport(SessionRow row)
    {
        var expected = ExpectedCash(
            row.OpeningFloat, row.SaleCash, row.SettlementCash, row.CashIn, row.CashOut, row.CancellationCashRefunds, row.ReturnCashRefunds);
        return new CashSessionReport(
            row.Id, row.RegisterAccountId, row.RegisterName, row.Status, row.OpenedByName, ToOffset(row.OpenedAt),
            row.ClosedByName, row.ClosedAt is { } closedAt ? ToOffset(closedAt) : null,
            row.OpeningFloat, row.SaleCash, row.SaleCard, row.SettlementCash, row.SettlementCard, row.CashIn, row.CashOut,
            row.CancellationCashRefunds, row.CancellationCardRefunds, row.ReturnCashRefunds, row.ReturnCardRefunds,
            expected, row.CountedCash, row.CountedCash - expected, (int)row.PendingCardCount);
    }

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private sealed class SessionRow
    {
        public Guid Id { get; init; }
        public Guid RegisterAccountId { get; init; }
        public string RegisterName { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string OpenedByName { get; init; } = string.Empty;
        public DateTime OpenedAt { get; init; }
        public string? ClosedByName { get; init; }
        public DateTime? ClosedAt { get; init; }
        public decimal OpeningFloat { get; init; }
        public decimal? CountedCash { get; init; }
        public decimal SaleCash { get; init; }
        public decimal SaleCard { get; init; }
        public decimal SettlementCash { get; init; }
        public decimal SettlementCard { get; init; }
        public decimal CashIn { get; init; }
        public decimal CashOut { get; init; }
        public decimal CancellationCashRefunds { get; init; }
        public decimal CancellationCardRefunds { get; init; }
        public decimal ReturnCashRefunds { get; init; }
        public decimal ReturnCardRefunds { get; init; }
        public long PendingCardCount { get; init; }
    }

    private sealed class TotalsRow
    {
        public long SalesCount { get; init; }
        public decimal Subtotal { get; init; }
        public decimal Discounts { get; init; }
        public decimal CashSales { get; init; }
        public decimal CardSales { get; init; }
        public decimal UnpaidSales { get; init; }
        public long UnpaidCount { get; init; }
        public long CancelledCount { get; init; }
        public decimal CancelledTotal { get; init; }
        public long CardRefusedCount { get; init; }
        public decimal CardRefusedTotal { get; init; }
        public long ReturnsCount { get; init; }
        public decimal ReturnsTotal { get; init; }
        public decimal ReturnCashRefunds { get; init; }
        public decimal ReturnCardRefunds { get; init; }
        public decimal SaleCash { get; init; }
        public decimal SaleCard { get; init; }
        public decimal OldReceivablesCash { get; init; }
        public decimal OldReceivablesCard { get; init; }
        public decimal SameDayReceivablesCash { get; init; }
        public decimal SameDayReceivablesCard { get; init; }
        public long SettledCount { get; init; }
        public decimal SettledAmount { get; init; }
        public decimal CancellationCashRefunds { get; init; }
        public decimal CancellationCardRefunds { get; init; }
        public decimal CashIn { get; init; }
        public decimal CashOut { get; init; }
        public long OutstandingCount { get; init; }
        public decimal OutstandingAmount { get; init; }
    }
}
