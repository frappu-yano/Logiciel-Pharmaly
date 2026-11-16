using System.Data;
using Dapper;
using Pharmaly.Contracts;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Receivables;

/// <summary>Lecture des créances : tableau des impayés, indicateurs, règlements.</summary>
public static class ReceivableQueries
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    /// <summary>
    /// Une ligne du tableau par créance. « act » est l'unique règlement actif de la créance :
    /// confirmé (créance soldée) ou en attente de la réponse du TPE.
    /// </summary>
    public const string LineSql =
        """
        SELECT json_build_object(
            'id', r.id, 'saleId', s.id, 'ticketNumber', s.ticket_number, 'ticketBarcode', s.ticket_barcode,
            'createdAt', r.created_at, 'customerId', c.id, 'customerName', c.name, 'amount', r.amount,
            'status', r.status, 'employeeId', s.employee_id, 'employeeName', e.full_name,
            'settledAt', r.settled_at,
            'settlementMethod', CASE WHEN act.status = 'CONFIRME' THEN act.method END,
            'settledByName', CASE WHEN act.status = 'CONFIRME' THEN act.employee_name END,
            'settlementId', CASE WHEN act.status = 'CONFIRME' THEN act.id END,
            'pendingSettlementId', CASE WHEN act.status = 'EN_ATTENTE' THEN act.id END)::text
        FROM receivables r
        JOIN sales s ON s.id = r.sale_id
        JOIN customers c ON c.id = r.customer_id
        JOIN employees e ON e.id = s.employee_id
        LEFT JOIN LATERAL (
            SELECT st.id, st.method, st.status, se.full_name AS employee_name
            FROM receivable_settlement_items i
            JOIN receivable_settlements st ON st.id = i.settlement_id
            JOIN employees se ON se.id = st.employee_id
            WHERE i.receivable_id = r.id AND i.is_active) act ON true
        """;

    public static Task<List<ReceivableLine>> ListAsync(IDbConnection connection, ReceivableFilter filter) =>
        SqlJson.ReadListAsync<ReceivableLine>(
            connection,
            LineSql +
            """

            WHERE (@Status::text IS NULL OR r.status = @Status)
              AND (@CustomerId::uuid IS NULL OR r.customer_id = @CustomerId)
              AND (@From::date IS NULL OR r.created_at >= @From::date)
              AND (@To::date IS NULL OR r.created_at < @To::date + 1)
              AND (@TicketPattern::text IS NULL OR s.ticket_number LIKE @TicketPattern OR s.ticket_barcode LIKE @TicketPattern)
            ORDER BY r.created_at DESC, s.ticket_number DESC
            LIMIT @Limit
            """,
            filter);

    public static Task<List<ReceivableLine>> ListForCustomerAsync(IDbConnection connection, Guid customerId) =>
        ListAsync(connection, new ReceivableFilter(null, customerId, null, null, null, MaxLimit));

    public static Task<ReceivableLine?> FindByTicketAsync(IDbConnection connection, string code) =>
        SqlJson.ReadSingleAsync<ReceivableLine>(
            connection, LineSql + " WHERE s.ticket_number = @code OR s.ticket_barcode = @code", new { code });

    public static async Task<ReceivableIndicators> ReadIndicatorsAsync(IDbConnection connection)
    {
        var row = await connection.QuerySingleAsync<(decimal OpenAmount, long OpenCount, decimal SettledAmount, long SettledCount)>(
            """
            SELECT COALESCE(sum(amount) FILTER (WHERE status = 'OUVERTE'), 0), count(*) FILTER (WHERE status = 'OUVERTE'),
                   COALESCE(sum(amount) FILTER (WHERE status = 'SOLDEE'), 0), count(*) FILTER (WHERE status = 'SOLDEE')
            FROM receivables
            """);
        return new ReceivableIndicators(
            row.OpenAmount, (int)row.OpenCount, row.OpenAmount + row.SettledAmount, (int)(row.OpenCount + row.SettledCount),
            row.SettledAmount, (int)row.SettledCount);
    }

    public static async Task<SettlementResult?> ReadSettlementAsync(
        IDbConnection connection, Guid settlementId, IDbTransaction? transaction = null)
    {
        var settlement = await SqlJson.ReadSingleAsync<SettlementResult>(
            connection,
            """
            SELECT json_build_object(
                'id', st.id, 'customerId', st.customer_id, 'customerName', c.name, 'method', st.method,
                'status', st.status, 'amount', st.amount, 'amountReceived', st.amount_received,
                'changeGiven', st.change_given, 'cashSessionId', st.cash_session_id,
                'employeeId', st.employee_id, 'employeeName', e.full_name,
                'createdAt', st.created_at, 'confirmedAt', st.confirmed_at, 'receivables', '[]'::json)::text
            FROM receivable_settlements st
            JOIN customers c ON c.id = st.customer_id
            JOIN employees e ON e.id = st.employee_id
            WHERE st.id = @settlementId
            """,
            new { settlementId },
            transaction);
        if (settlement is null)
        {
            return null;
        }

        var lines = await SqlJson.ReadListAsync<ReceivableLine>(
            connection,
            LineSql +
            """

            WHERE r.id IN (SELECT receivable_id FROM receivable_settlement_items WHERE settlement_id = @settlementId)
            ORDER BY s.ticket_number
            """,
            new { settlementId },
            transaction);
        return settlement with { Receivables = lines };
    }
}

/// <summary>Filtres du tableau des impayés ; les dates sont des jours entiers, bornes comprises.</summary>
public sealed record ReceivableFilter(
    string? Status,
    Guid? CustomerId,
    DateTime? From,
    DateTime? To,
    string? TicketPattern,
    int Limit);
