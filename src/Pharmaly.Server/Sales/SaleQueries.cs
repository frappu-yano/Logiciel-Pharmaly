using System.Data;
using Pharmaly.Contracts;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Sales;

/// <summary>Lecture des tickets : détail complet et historique.</summary>
public static class SaleQueries
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private const string DetailSql =
        """
        SELECT json_build_object(
            'id', s.id, 'ticketNumber', s.ticket_number, 'ticketBarcode', s.ticket_barcode,
            'storeId', s.store_id, 'storeName', st.name, 'registerName', ra.display_name,
            'cashSessionId', s.cash_session_id, 'employeeId', s.employee_id, 'employeeName', e.full_name,
            'createdAt', s.created_at, 'status', s.status, 'paymentMode', s.payment_mode,
            'isUnpaid', COALESCE(r.status = 'OUVERTE', false),
            'subtotal', s.subtotal, 'discountKind', s.discount_kind, 'discountValue', s.discount_value,
            'discountAmount', s.discount_amount, 'total', s.total,
            'customerId', s.customer_id, 'customerName', c.name,
            'lines', (SELECT COALESCE(json_agg(json_build_object(
                          'lineNumber', l.line_number, 'productId', l.product_id, 'productName', l.product_name,
                          'quantity', l.quantity, 'unitPrice', l.unit_price, 'lineTotal', l.line_total)
                          ORDER BY l.line_number), '[]'::json)
                      FROM sale_lines l WHERE l.sale_id = s.id),
            'payment', (SELECT json_build_object(
                          'id', p.id, 'method', p.method, 'status', p.status, 'amount', p.amount,
                          'amountReceived', p.amount_received, 'changeGiven', p.change_given,
                          'cashSessionId', p.cash_session_id, 'employeeId', p.employee_id, 'employeeName', pe.full_name,
                          'createdAt', p.created_at, 'confirmedAt', p.confirmed_at)
                        FROM payments p JOIN employees pe ON pe.id = p.employee_id WHERE p.sale_id = s.id),
            'receivable', CASE WHEN r.id IS NULL THEN NULL ELSE json_build_object(
                          'id', r.id, 'status', r.status, 'amount', r.amount, 'settledAt', r.settled_at) END,
            'cancellation', (SELECT json_build_object(
                          'kind', x.kind, 'reason', x.reason, 'employeeId', x.employee_id, 'employeeName', xe.full_name,
                          'occurredAt', x.occurred_at, 'refundMethod', x.refund_method, 'refundAmount', x.refund_amount)
                        FROM sale_cancellations x JOIN employees xe ON xe.id = x.employee_id WHERE x.sale_id = s.id)
        )::text
        FROM sales s
        JOIN stores st ON st.id = s.store_id
        JOIN register_accounts ra ON ra.id = s.register_account_id
        JOIN employees e ON e.id = s.employee_id
        LEFT JOIN customers c ON c.id = s.customer_id
        LEFT JOIN receivables r ON r.sale_id = s.id
        """;

    public static Task<SaleDetail?> ReadDetailAsync(
        IDbConnection connection, Guid saleId, Guid storeId, IDbTransaction? transaction = null) =>
        SqlJson.ReadSingleAsync<SaleDetail>(
            connection, DetailSql + " WHERE s.id = @saleId AND s.store_id = @storeId", new { saleId, storeId }, transaction);

    /// <summary>Recherche exacte par numéro de ticket ou par valeur du code-barres imprimé.</summary>
    public static Task<SaleDetail?> FindByTicketAsync(IDbConnection connection, string numberOrBarcode, Guid storeId) =>
        SqlJson.ReadSingleAsync<SaleDetail>(
            connection,
            DetailSql + " WHERE s.store_id = @storeId AND (s.ticket_number = @code OR s.ticket_barcode = @code)",
            new { code = NormalizeTicketCode(numberOrBarcode), storeId });

    public static Task<List<SaleSummary>> ListAsync(IDbConnection connection, SaleFilter filter) =>
        SqlJson.ReadListAsync<SaleSummary>(
            connection,
            """
            SELECT json_build_object(
                'id', s.id, 'ticketNumber', s.ticket_number, 'ticketBarcode', s.ticket_barcode,
                'createdAt', s.created_at, 'status', s.status, 'paymentMode', s.payment_mode,
                'isUnpaid', COALESCE(r.status = 'OUVERTE', false), 'total', s.total,
                'employeeId', s.employee_id, 'employeeName', e.full_name, 'customerName', c.name)::text
            FROM sales s
            JOIN employees e ON e.id = s.employee_id
            LEFT JOIN customers c ON c.id = s.customer_id
            LEFT JOIN receivables r ON r.sale_id = s.id
            WHERE s.store_id = @StoreId
              AND (@From::date IS NULL OR s.created_at >= @From::date)
              AND (@To::date IS NULL OR s.created_at < @To::date + 1)
              AND (@EmployeeId::uuid IS NULL OR s.employee_id = @EmployeeId)
              AND (@Status::text IS NULL OR s.status = @Status)
              AND (@PaymentMode::text IS NULL OR s.payment_mode = @PaymentMode)
              AND (@TicketPattern::text IS NULL OR s.ticket_number LIKE @TicketPattern OR s.ticket_barcode LIKE @TicketPattern)
            ORDER BY s.created_at DESC, s.ticket_seq DESC
            LIMIT @Limit
            """,
            filter);

    /// <summary>Les numéros et codes-barres de ticket sont en majuscules, sans espace.</summary>
    public static string NormalizeTicketCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Motif LIKE « contient », ou null si rien n'est saisi.</summary>
    public static string? ToTicketPattern(string? term)
    {
        var cleaned = NormalizeTicketCode(term);
        return cleaned.Length == 0
            ? null
            : "%" + cleaned.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }
}

/// <summary>Filtres de l'historique des ventes ; les dates sont des jours entiers, bornes comprises.</summary>
public sealed record SaleFilter(
    Guid StoreId,
    DateTime? From,
    DateTime? To,
    Guid? EmployeeId,
    string? Status,
    string? PaymentMode,
    string? TicketPattern,
    int Limit);
