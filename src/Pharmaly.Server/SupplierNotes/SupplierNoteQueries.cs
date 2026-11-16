using System.Data;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;

namespace Pharmaly.Server.SupplierNotes;

/// <summary>Lectures des bons fournisseurs, hors transaction ou dans celle d'une commande.</summary>
public sealed class SupplierNoteQueries(NpgsqlDataSource dataSource)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxSuppliers = 500;

    // Les lignes annulées ne comptent ni dans le nombre de lignes ni dans le total.
    private const string SummarySelect =
        """
        SELECT n.id AS Id, n.number AS Number, n.supplier_id AS SupplierId, s.name AS SupplierName,
               n.location_id AS LocationId, l.name AS LocationName, st.id AS StoreId, st.name AS StoreName,
               n.supplier_reference AS SupplierReference, n.note_date::timestamp AS NoteDate, n.status AS Status,
               (SELECT count(*)::int FROM supplier_note_lines x WHERE x.note_id = n.id AND NOT x.is_cancelled) AS LineCount,
               COALESCE((SELECT sum(round(x.quantity * x.purchase_price, 2)) FROM supplier_note_lines x
                         WHERE x.note_id = n.id AND NOT x.is_cancelled), 0) AS TotalAmount,
               n.created_at AS CreatedAt, creator.full_name AS CreatedByName,
               n.lock_employee_id AS LockEmployeeId, holder.full_name AS LockEmployeeName,
               ra.display_name AS LockRegisterName, n.lock_acquired_at AS LockAcquiredAt,
               n.lock_expires_at AS LockExpiresAt,
               COALESCE(n.lock_expires_at <= clock_timestamp(), false) AS LockExpired
        FROM supplier_notes n
        JOIN suppliers s ON s.id = n.supplier_id
        JOIN stock_locations l ON l.id = n.location_id
        JOIN stores st ON st.id = l.store_id
        JOIN employees creator ON creator.id = n.created_by
        LEFT JOIN employees holder ON holder.id = n.lock_employee_id
        LEFT JOIN register_accounts ra ON ra.id = n.lock_register_account_id
        """;

    private const string LineSelect =
        """
        SELECT x.id AS Id, x.position AS Position, x.product_id AS ProductId, p.internal_code AS ProductCode,
               p.name AS ProductName,
               (SELECT b.barcode FROM product_barcodes b WHERE b.product_id = p.id
                 ORDER BY b.is_primary DESC, b.barcode LIMIT 1) AS Barcode,
               x.quantity AS Quantity, x.purchase_price AS PurchasePrice, x.version AS Version,
               x.is_cancelled AS IsCancelled, p.sale_price AS SalePrice,
               x.previous_purchase_price AS PreviousPurchasePrice
        FROM supplier_note_lines x
        JOIN products p ON p.id = x.product_id
        """;

    // Dernier prix d'achat connu : celui de la ligne la plus récemment enregistrée, hors
    // lignes annulées et bons annulés.
    private const string ProductInfoSelect =
        """
        SELECT p.id AS Id, p.internal_code AS Code, p.name AS Name,
               (SELECT b.barcode FROM product_barcodes b WHERE b.product_id = p.id
                 ORDER BY b.is_primary DESC, b.barcode LIMIT 1) AS Barcode,
               p.sale_price AS SalePrice, p.sold_by_unit AS SoldByUnit, p.is_active AS IsActive,
               last.purchase_price AS LastPurchasePrice, last.supplier_name AS LastSupplierName,
               last.note_date::timestamp AS LastPurchaseDate
        FROM products p
        LEFT JOIN LATERAL (
            SELECT x.purchase_price, s.name AS supplier_name, n.note_date
            FROM supplier_note_lines x
            JOIN supplier_notes n ON n.id = x.note_id
            JOIN suppliers s ON s.id = n.supplier_id
            WHERE x.product_id = p.id AND NOT x.is_cancelled AND n.status <> 'ANNULE'
            ORDER BY x.created_at DESC, n.number DESC, x.position DESC
            LIMIT 1) last ON true
        """;

    public async Task<IReadOnlyList<SupplierSummary>> ListSuppliersAsync(string? term)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var cleaned = (term ?? string.Empty).Trim();
        return (await connection.QueryAsync<SupplierSummary>(
            """
            SELECT id AS Id, name AS Name FROM suppliers
            WHERE is_active AND (@pattern = '' OR position(@pattern IN lower(name)) > 0)
            ORDER BY lower(name)
            LIMIT @MaxSuppliers
            """,
            new { pattern = cleaned.ToLowerInvariant(), MaxSuppliers })).AsList();
    }

    /// <summary>Bons des sites couverts par les droits de l'employé, du plus récent au plus ancien.</summary>
    public async Task<SupplierNotePage> ListAsync(
        Guid employeeId, string? status, Guid? supplierId, DateOnly? from, DateOnly? to, int page, int pageSize)
    {
        var boundedSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var boundedPage = Math.Max(page, 1);
        const string Filter =
            """
            WHERE (@status::text IS NULL OR n.status = @status)
              AND (@supplierId::uuid IS NULL OR n.supplier_id = @supplierId)
              AND (@from::date IS NULL OR n.note_date >= @from::date)
              AND (@to::date IS NULL OR n.note_date <= @to::date)
              AND EXISTS (SELECT 1 FROM employee_permissions ep
                          WHERE ep.employee_id = @employeeId AND ep.permission_code = ANY(@rights)
                            AND (ep.store_id IS NULL OR ep.store_id = l.store_id))
            """;
        var parameters = new
        {
            status,
            supplierId,
            from = from?.ToDateTime(TimeOnly.MinValue),
            to = to?.ToDateTime(TimeOnly.MinValue),
            employeeId,
            rights = NoteAccess.ReadRights,
            limit = boundedSize,
            offset = (long)(boundedPage - 1) * boundedSize,
        };

        await using var connection = await dataSource.OpenConnectionAsync();
        var total = await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*)::int FROM supplier_notes n JOIN stock_locations l ON l.id = n.location_id {Filter}", parameters);
        var rows = await connection.QueryAsync<NoteRow>(
            $"{SummarySelect} {Filter} ORDER BY n.note_date DESC, n.number DESC LIMIT @limit OFFSET @offset", parameters);
        return new SupplierNotePage(rows.Select(row => row.ToSummary(employeeId)).ToList(), total, boundedPage, boundedSize);
    }

    public async Task<SupplierNoteDetail?> ReadDetailAsync(Guid noteId, Guid employeeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await ReadDetailAsync(connection, null, noteId, employeeId);
    }

    public static async Task<SupplierNoteSummary?> ReadSummaryAsync(
        IDbConnection connection, IDbTransaction? transaction, Guid noteId, Guid employeeId) =>
        (await connection.QuerySingleOrDefaultAsync<NoteRow>(
            $"{SummarySelect} WHERE n.id = @noteId", new { noteId }, transaction))?.ToSummary(employeeId);

    public static async Task<SupplierNoteDetail?> ReadDetailAsync(
        IDbConnection connection, IDbTransaction? transaction, Guid noteId, Guid employeeId)
    {
        var summary = await ReadSummaryAsync(connection, transaction, noteId, employeeId);
        if (summary is null)
        {
            return null;
        }

        var lines = await connection.QueryAsync<LineRow>(
            $"{LineSelect} WHERE x.note_id = @noteId ORDER BY x.position", new { noteId }, transaction);
        var history = await connection.QueryAsync<HistoryRow>(
            """
            SELECT h.occurred_at AS OccurredAt, h.event AS Event, h.status_before AS StatusBefore,
                   h.status_after AS StatusAfter, e.full_name AS EmployeeName, h.line_id AS LineId,
                   h.description AS Description
            FROM supplier_note_history h JOIN employees e ON e.id = h.employee_id
            WHERE h.note_id = @noteId
            ORDER BY h.id
            """,
            new { noteId },
            transaction);
        return new SupplierNoteDetail(
            summary,
            lines.Select(line => line.ToLine()).ToList(),
            history.Select(entry => new SupplierNoteHistoryEntry(
                ToOffset(entry.OccurredAt), entry.Event, entry.StatusBefore, entry.StatusAfter, entry.EmployeeName,
                entry.LineId, entry.Description)).ToList());
    }

    public static async Task<SupplierNoteLine> ReadLineAsync(IDbConnection connection, IDbTransaction transaction, Guid lineId) =>
        (await connection.QuerySingleAsync<LineRow>($"{LineSelect} WHERE x.id = @lineId", new { lineId }, transaction)).ToLine();

    public async Task<NoteProductInfo?> FindProductAsync(Guid productId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await ReadProductAsync(connection, null, productId);
    }

    public async Task<NoteProductInfo?> FindProductByBarcodeAsync(string barcode)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return (await connection.QuerySingleOrDefaultAsync<ProductInfoRow>(
            $"{ProductInfoSelect} JOIN product_barcodes scanned ON scanned.product_id = p.id WHERE scanned.barcode = @barcode",
            new { barcode = barcode.Trim() }))?.ToInfo();
    }

    public static async Task<NoteProductInfo?> ReadProductAsync(IDbConnection connection, IDbTransaction? transaction, Guid productId) =>
        (await connection.QuerySingleOrDefaultAsync<ProductInfoRow>(
            $"{ProductInfoSelect} WHERE p.id = @productId", new { productId }, transaction))?.ToInfo();

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private sealed class NoteRow
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public Guid SupplierId { get; init; }
        public string SupplierName { get; init; } = string.Empty;
        public Guid LocationId { get; init; }
        public string LocationName { get; init; } = string.Empty;
        public Guid StoreId { get; init; }
        public string StoreName { get; init; } = string.Empty;
        public string SupplierReference { get; init; } = string.Empty;
        public DateTime NoteDate { get; init; }
        public string Status { get; init; } = string.Empty;
        public int LineCount { get; init; }
        public decimal TotalAmount { get; init; }
        public DateTime CreatedAt { get; init; }
        public string CreatedByName { get; init; } = string.Empty;
        public Guid? LockEmployeeId { get; init; }
        public string? LockEmployeeName { get; init; }
        public string? LockRegisterName { get; init; }
        public DateTime? LockAcquiredAt { get; init; }
        public DateTime? LockExpiresAt { get; init; }
        public bool LockExpired { get; init; }

        public SupplierNoteSummary ToSummary(Guid employeeId) => new(
            Id, Number, SupplierId, SupplierName, LocationId, LocationName, StoreId, StoreName, SupplierReference,
            DateOnly.FromDateTime(NoteDate), Status, LineCount, TotalAmount, ToOffset(CreatedAt), CreatedByName,
            LockEmployeeId is { } holder
                ? new SupplierNoteLock(
                    holder, LockEmployeeName!, LockRegisterName!, ToOffset(LockAcquiredAt!.Value),
                    ToOffset(LockExpiresAt!.Value), holder == employeeId, LockExpired)
                : null);
    }

    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public int Position { get; init; }
        public Guid ProductId { get; init; }
        public long ProductCode { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public decimal Quantity { get; init; }
        public decimal PurchasePrice { get; init; }
        public int Version { get; init; }
        public bool IsCancelled { get; init; }
        public decimal SalePrice { get; init; }
        public decimal? PreviousPurchasePrice { get; init; }

        public SupplierNoteLine ToLine() => new(
            Id, Position, ProductId, ProductCode, ProductName, Barcode, Quantity, PurchasePrice,
            MarginCalculator.LineTotal(Quantity, PurchasePrice), Version, IsCancelled,
            MarginCalculator.Compute(SalePrice, PreviousPurchasePrice, PurchasePrice));
    }

    private sealed class HistoryRow
    {
        public DateTime OccurredAt { get; init; }
        public string Event { get; init; } = string.Empty;
        public string? StatusBefore { get; init; }
        public string StatusAfter { get; init; } = string.Empty;
        public string EmployeeName { get; init; } = string.Empty;
        public Guid? LineId { get; init; }
        public string Description { get; init; } = string.Empty;
    }

    private sealed class ProductInfoRow
    {
        public Guid Id { get; init; }
        public long Code { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public decimal SalePrice { get; init; }
        public bool SoldByUnit { get; init; }
        public bool IsActive { get; init; }
        public decimal? LastPurchasePrice { get; init; }
        public string? LastSupplierName { get; init; }
        public DateTime? LastPurchaseDate { get; init; }

        public NoteProductInfo ToInfo() => new(
            Id, Code, Name, Barcode, SalePrice, SoldByUnit, IsActive, LastPurchasePrice, LastSupplierName,
            LastPurchaseDate is { } date ? DateOnly.FromDateTime(date) : null);
    }
}
