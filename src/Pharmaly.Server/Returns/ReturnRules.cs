using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Operations;
using Pharmaly.Server.Stock;

namespace Pharmaly.Server.Returns;

/// <summary>Une ligne du ticket, avec ce qui en a déjà été retourné et ce qui a été payé pour elle.</summary>
public sealed record ReturnSaleLine(
    long SaleLineId,
    int LineNumber,
    Guid ProductId,
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    decimal Returned,
    decimal Refunded,
    bool SoldByUnit,
    decimal Paid);

public sealed record ReturnSaleState(
    Guid Id,
    string TicketNumber,
    string TicketBarcode,
    DateTime CreatedAt,
    string Status,
    string PaymentMode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    string? CustomerName,
    string? RefundMethod,
    BusinessException? Blocked,
    IReadOnlyList<ReturnSaleLine> Lines);

public sealed record ComputedReturnLine(ReturnSaleLine SaleLine, decimal Quantity, decimal Refund);

public sealed record ComputedReturn(
    ReturnSaleState Sale, ReturnReason Reason, string? RefundMethod, decimal RefundAmount, IReadOnlyList<ComputedReturnLine> Lines);

/// <summary>
/// Règles d'un retour, communes à l'aperçu (rien n'est écrit) et à l'enregistrement
/// (le ticket est alors verrouillé : deux retours du même ticket passent l'un après l'autre,
/// le second voit les quantités déjà retournées par le premier).
/// </summary>
public static class ReturnRules
{
    public const int MaxLines = 200;
    private const int MaxQuantityDecimals = 3;

    /// <summary>
    /// État du ticket pour un retour. <paramref name="lockSale"/> verrouille le ticket puis sa
    /// créance jusqu'à la fin de la transaction (même ordre que l'annulation d'une vente).
    /// </summary>
    public static async Task<ReturnSaleState?> ReadSaleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId, Guid? saleId, string? ticketCode, bool lockSale)
    {
        var sale = await connection.QuerySingleOrDefaultAsync<SaleRow>(
            $"""
             SELECT s.id AS Id, s.ticket_number AS TicketNumber, s.ticket_barcode AS TicketBarcode, s.created_at AS CreatedAt,
                    s.status AS Status, s.payment_mode AS PaymentMode, s.subtotal AS Subtotal,
                    s.discount_amount AS DiscountAmount, s.total AS Total,
                    (SELECT c.name FROM customers c WHERE c.id = s.customer_id) AS CustomerName
             FROM sales s
             WHERE s.store_id = @storeId
               AND (s.id = @saleId OR (@saleId::uuid IS NULL AND (s.ticket_number = @ticketCode OR s.ticket_barcode = @ticketCode)))
             {(lockSale ? "FOR UPDATE OF s" : string.Empty)}
             """,
            new { storeId, saleId, ticketCode },
            transaction);
        if (sale is null)
        {
            return null;
        }

        BusinessException? blocked = null;
        string? refundMethod = null;
        if (sale.Status != SaleStatuses.Completed)
        {
            blocked = new BusinessException(
                ReturnErrorCodes.SaleNotReturnable,
                sale.Status == SaleStatuses.Cancelled
                    ? $"Le ticket {sale.TicketNumber} est annulé : il n'y a rien à retourner."
                    : $"Le paiement par carte du ticket {sale.TicketNumber} attend sa réponse : indiquez d'abord « accepté » ou « refusé ».",
                StatusCodes.Status409Conflict);
        }
        else if (sale.PaymentMode == SalePaymentModes.Unpaid)
        {
            // Vente impayée : un retour n'est possible qu'une fois la créance réglée en entier.
            // Le remboursement suit alors le moyen du règlement.
            var receivable = await connection.QuerySingleAsync<(string Status, string? Method)>(
                $"""
                 SELECT r.status,
                        (SELECT st.method FROM receivable_settlement_items i
                         JOIN receivable_settlements st ON st.id = i.settlement_id
                         WHERE i.receivable_id = r.id AND st.status = 'CONFIRME')
                 FROM receivables r WHERE r.sale_id = @Id
                 {(lockSale ? "FOR SHARE OF r" : string.Empty)}
                 """,
                new { sale.Id },
                transaction);
            if (receivable.Status == ReceivableStatuses.Settled)
            {
                refundMethod = receivable.Method;
            }
            else
            {
                blocked = new BusinessException(
                    ReturnErrorCodes.ReceivableOpen,
                    $"Le ticket {sale.TicketNumber} est impayé : aucun retour tant que sa créance n'est pas réglée en entier. "
                    + "Si la marchandise revient avant le règlement, annulez la vente (possible tant que sa journée n'est pas clôturée) ; "
                    + "sinon, encaissez d'abord le ticket, puis faites le retour.",
                    StatusCodes.Status409Conflict);
            }
        }
        else
        {
            refundMethod = sale.PaymentMode;
        }

        var rows = (await connection.QueryAsync<LineRow>(
            """
            SELECT l.id AS SaleLineId, l.line_number AS LineNumber, l.product_id AS ProductId, l.product_name AS ProductName,
                   l.quantity AS Quantity, l.unit_price AS UnitPrice, l.line_total AS LineTotal,
                   COALESCE((SELECT sum(rl.quantity) FROM return_lines rl WHERE rl.sale_line_id = l.id), 0) AS Returned,
                   COALESCE((SELECT sum(rl.refund_amount) FROM return_lines rl WHERE rl.sale_line_id = l.id), 0) AS Refunded,
                   p.sold_by_unit AS SoldByUnit
            FROM sale_lines l JOIN products p ON p.id = l.product_id
            WHERE l.sale_id = @Id
            ORDER BY l.line_number
            """,
            new { sale.Id },
            transaction)).AsList();
        var paid = ReturnMath.PaidPerLine(rows.Select(row => row.LineTotal).ToList(), sale.Subtotal, sale.Total);
        var lines = rows.Select((row, index) => new ReturnSaleLine(
            row.SaleLineId, row.LineNumber, row.ProductId, row.ProductName, row.Quantity, row.UnitPrice, row.LineTotal,
            row.Returned, row.Refunded, row.SoldByUnit, paid[index])).ToList();

        return new ReturnSaleState(
            sale.Id, sale.TicketNumber, sale.TicketBarcode, sale.CreatedAt, sale.Status, sale.PaymentMode, sale.Subtotal,
            sale.DiscountAmount, sale.Total, sale.CustomerName, refundMethod, blocked, lines);
    }

    /// <summary>Contrôle la demande et calcule le remboursement. Refuse par une <see cref="BusinessException"/>.</summary>
    public static async Task<ComputedReturn> ComputeAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid storeId,
        Guid saleId, Guid reasonId, IReadOnlyList<ReturnLineRequest>? requestedLines, bool lockSale)
    {
        var requested = requestedLines ?? [];
        if (requested.Count is 0 or > MaxLines)
        {
            throw BusinessException.Invalid($"Un retour contient de 1 à {MaxLines} lignes.");
        }

        if (requested.Any(line => line.Quantity <= 0 || decimal.Round(line.Quantity, MaxQuantityDecimals) != line.Quantity))
        {
            throw BusinessException.Invalid($"Chaque quantité retournée doit être positive, avec au plus {MaxQuantityDecimals} décimales.");
        }

        if (requested.Select(line => line.ProductId).Distinct().Count() != requested.Count)
        {
            throw BusinessException.Invalid("Le même produit figure sur plusieurs lignes du retour : regroupez les quantités.");
        }

        var sale = await ReadSaleAsync(connection, transaction, storeId, saleId, null, lockSale)
            ?? throw BusinessException.NotFound("Ticket introuvable.");
        if (sale.Blocked is not null)
        {
            throw sale.Blocked;
        }

        var reason = await connection.QuerySingleOrDefaultAsync<ReturnReason>(
            ReturnQueries.ReasonSql + " WHERE id = @reasonId", new { reasonId }, transaction);
        if (reason is null || !reason.IsActive)
        {
            throw new BusinessException(
                ReturnErrorCodes.InvalidReason, "Choisissez un motif de retour dans la liste des motifs actifs.");
        }

        var byProduct = sale.Lines.ToDictionary(line => line.ProductId);
        var lines = new List<ComputedReturnLine>(requested.Count);
        foreach (var line in requested)
        {
            if (!byProduct.TryGetValue(line.ProductId, out var sold))
            {
                throw BusinessException.Invalid($"Un des produits ne figure pas sur le ticket {sale.TicketNumber}.");
            }

            if (sold.SoldByUnit && line.Quantity != decimal.Truncate(line.Quantity))
            {
                throw new BusinessException(
                    ApiErrorCodes.FractionalQuantity,
                    $"« {sold.ProductName} » se vend à l'unité : la quantité {StockEngine.Format(line.Quantity)} n'est pas entière.");
            }

            var returnable = sold.Quantity - sold.Returned;
            if (line.Quantity > returnable)
            {
                throw new BusinessException(
                    ReturnErrorCodes.QuantityExceeded,
                    $"« {sold.ProductName} » : {StockEngine.Format(line.Quantity)} demandé en retour, "
                    + $"{StockEngine.Format(returnable)} encore retournable "
                    + $"({StockEngine.Format(sold.Quantity)} vendu, {StockEngine.Format(sold.Returned)} déjà retourné).",
                    StatusCodes.Status409Conflict);
            }

            lines.Add(new ComputedReturnLine(
                sold, line.Quantity, ReturnMath.RefundForLine(sold.Paid, sold.Quantity, sold.Returned, line.Quantity)));
        }

        var refund = lines.Sum(line => line.Refund);
        return new ComputedReturn(sale, reason, refund > 0 ? sale.RefundMethod : null, refund, lines);
    }

    private sealed class SaleRow
    {
        public Guid Id { get; init; }
        public string TicketNumber { get; init; } = string.Empty;
        public string TicketBarcode { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string Status { get; init; } = string.Empty;
        public string PaymentMode { get; init; } = string.Empty;
        public decimal Subtotal { get; init; }
        public decimal DiscountAmount { get; init; }
        public decimal Total { get; init; }
        public string? CustomerName { get; init; }
    }

    private sealed class LineRow
    {
        public long SaleLineId { get; init; }
        public int LineNumber { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal LineTotal { get; init; }
        public decimal Returned { get; init; }
        public decimal Refunded { get; init; }
        public bool SoldByUnit { get; init; }
    }
}
