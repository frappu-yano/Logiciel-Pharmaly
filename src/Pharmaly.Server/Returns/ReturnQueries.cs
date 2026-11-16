using System.Data;
using Dapper;
using Pharmaly.Contracts;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Returns;

/// <summary>Lecture des retours et des motifs.</summary>
public static class ReturnQueries
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public const string ReasonSql =
        """
        SELECT id AS Id, label AS Label, stock_destination AS StockDestination, is_active AS IsActive, sort_order AS SortOrder
        FROM return_reasons
        """;

    private const string DetailSql =
        """
        SELECT json_build_object(
            'id', r.id, 'returnNumber', r.return_number, 'saleId', r.sale_id, 'ticketNumber', s.ticket_number,
            'storeId', r.store_id, 'registerName', ra.display_name, 'cashSessionId', r.cash_session_id,
            'employeeId', r.employee_id, 'employeeName', e.full_name, 'createdAt', r.created_at,
            'reasonId', r.reason_id, 'reasonLabel', r.reason_label, 'stockDestination', r.stock_destination,
            'destinationLocationId', r.destination_location_id, 'destinationLocationName', loc.name,
            'note', r.note, 'refundMethod', r.refund_method, 'refundAmount', r.refund_amount,
            'lines', (SELECT COALESCE(json_agg(json_build_object(
                          'lineNumber', l.line_number, 'productId', l.product_id, 'productName', l.product_name,
                          'quantity', l.quantity, 'unitPrice', l.unit_price, 'refundAmount', l.refund_amount,
                          'restockedQuantity', l.restocked_quantity,
                          'notRestockedQuantity', l.quantity - l.restocked_quantity)
                          ORDER BY l.line_number), '[]'::json)
                      FROM return_lines l WHERE l.return_id = r.id)
        )::text
        FROM returns r
        JOIN sales s ON s.id = r.sale_id
        JOIN register_accounts ra ON ra.id = r.register_account_id
        JOIN employees e ON e.id = r.employee_id
        LEFT JOIN stock_locations loc ON loc.id = r.destination_location_id
        """;

    public static async Task<IReadOnlyList<ReturnReason>> ListReasonsAsync(IDbConnection connection, bool includeInactive) =>
        (await connection.QueryAsync<ReturnReason>(
            ReasonSql + " WHERE is_active OR @includeInactive ORDER BY sort_order, label", new { includeInactive })).AsList();

    public static Task<ReturnDetail?> ReadDetailAsync(
        IDbConnection connection, Guid returnId, Guid storeId, IDbTransaction? transaction = null) =>
        SqlJson.ReadSingleAsync<ReturnDetail>(
            connection, DetailSql + " WHERE r.id = @returnId AND r.store_id = @storeId", new { returnId, storeId }, transaction);

    public static Task<List<ReturnSummary>> ListAsync(IDbConnection connection, ReturnFilter filter) =>
        SqlJson.ReadListAsync<ReturnSummary>(
            connection,
            """
            SELECT json_build_object(
                'id', r.id, 'returnNumber', r.return_number, 'saleId', r.sale_id, 'ticketNumber', s.ticket_number,
                'createdAt', r.created_at, 'reasonLabel', r.reason_label, 'stockDestination', r.stock_destination,
                'refundMethod', r.refund_method, 'refundAmount', r.refund_amount,
                'employeeId', r.employee_id, 'employeeName', e.full_name)::text
            FROM returns r
            JOIN sales s ON s.id = r.sale_id
            JOIN employees e ON e.id = r.employee_id
            WHERE r.store_id = @StoreId
              AND (@From::date IS NULL OR r.created_at >= @From::date)
              AND (@To::date IS NULL OR r.created_at < @To::date + 1)
              AND (@SaleId::uuid IS NULL OR r.sale_id = @SaleId)
              AND (@TicketPattern::text IS NULL OR s.ticket_number LIKE @TicketPattern OR s.ticket_barcode LIKE @TicketPattern
                   OR r.return_number LIKE @TicketPattern)
            ORDER BY r.created_at DESC, r.return_seq DESC
            LIMIT @Limit
            """,
            filter);
}

/// <summary>Filtres de l'historique des retours ; les dates sont des jours entiers, bornes comprises.</summary>
public sealed record ReturnFilter(Guid StoreId, DateTime? From, DateTime? To, Guid? SaleId, string? TicketPattern, int Limit);
