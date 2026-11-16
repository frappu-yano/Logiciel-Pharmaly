using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;
using Pharmaly.Server.Receivables;

namespace Pharmaly.Server.Customers;

/// <summary>Clients : création rapide pendant une vente, recherche, fiche avec ses créances.</summary>
public static class CustomerEndpoints
{
    public const string CreateOperation = "CLIENT_CREER";
    public const string CustomerCreatedAudit = "CLIENT_CREE";
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    private const int MaxNameLength = 200;
    private const int MaxPhoneLength = 30;
    private const int MaxTermLength = 80;

    private const string SummaryColumns =
        """
        c.id AS Id, c.name AS Name, c.phone AS Phone,
        COALESCE((SELECT sum(r.amount) FROM receivables r WHERE r.customer_id = c.id AND r.status = 'OUVERTE'), 0) AS OpenAmount,
        (SELECT count(*)::int FROM receivables r WHERE r.customer_id = c.id AND r.status = 'OUVERTE') AS OpenCount
        """;

    public static void MapCustomerEndpoints(this WebApplication app)
    {
        // Le client se crée pendant la vente : même droit que la vente.
        app.MapPost(CustomerRoutes.Customers, CreateAsync).RequirePermission(Permissions.SalesCreate);

        // La recherche sert à la vente impayée et à l'écran des impayés : l'un de ces droits suffit.
        app.MapGet(CustomerRoutes.CustomerSearch, async (string? q, int? limite, HttpContext context, NpgsqlDataSource dataSource) =>
        {
            if (await PermissionGuard.RefuseWithoutAnyAsync(
                    dataSource, context, Permissions.SalesCreate, Permissions.ReceivablesView, Permissions.ReceivablesSettle) is { } refusal)
            {
                return refusal;
            }

            var term = (q ?? string.Empty).Trim();
            if (term.Length > MaxTermLength)
            {
                term = term[..MaxTermLength];
            }

            // Chaque mot saisi doit apparaître dans le nom ; une suite de chiffres cherche aussi le téléphone.
            var patterns = TextNormalizer.ToSearchKey(term).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => "%" + EscapeLike(word) + "%").ToArray();
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
            await using var connection = await dataSource.OpenConnectionAsync();
            return Results.Ok(await connection.QueryAsync<CustomerSummary>(
                $"""
                 SELECT {SummaryColumns}
                 FROM customers c
                 WHERE c.search_name LIKE ALL (@patterns)
                    OR (@phonePattern::text IS NOT NULL AND regexp_replace(c.phone, '[^0-9]', '', 'g') LIKE @phonePattern)
                 ORDER BY c.name, c.created_at
                 LIMIT @limit
                 """,
                new
                {
                    patterns,
                    phonePattern = digits.Length >= 3 && digits.Length == term.Count(character => !char.IsWhiteSpace(character))
                        ? "%" + digits + "%"
                        : null,
                    limit = Math.Clamp(limite ?? DefaultLimit, 1, MaxLimit),
                }));
        }).RequireEmployee();

        app.MapGet(CustomerRoutes.Customers + "/{id:guid}", async (Guid id, NpgsqlDataSource dataSource) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            var customer = await connection.QuerySingleOrDefaultAsync<(string Name, string? Phone, DateTime CreatedAt)>(
                "SELECT name, phone, created_at FROM customers WHERE id = @id", new { id });
            if (customer.Name is null)
            {
                return Results.Json(
                    new ApiError(ApiErrorCodes.NotFound, "Client introuvable."), statusCode: StatusCodes.Status404NotFound);
            }

            var receivables = await ReceivableQueries.ListForCustomerAsync(connection, id);
            var open = receivables.Where(receivable => receivable.Status == ReceivableStatuses.Open).ToList();
            return Results.Ok(new CustomerDetail(
                id, customer.Name, customer.Phone, new DateTimeOffset(DateTime.SpecifyKind(customer.CreatedAt, DateTimeKind.Utc)),
                open.Sum(receivable => receivable.Amount), open.Count, receivables));
        }).RequirePermission(Permissions.ReceivablesView);
    }

    private static Task<IResult> CreateAsync(CreateCustomerRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, CreateOperation, context.GetIdentity(), request, async operation =>
        {
            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > MaxNameLength)
            {
                throw BusinessException.Invalid($"Le nom du client est obligatoire ({MaxNameLength} caractères au plus).");
            }

            var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
            if (phone?.Length > MaxPhoneLength)
            {
                throw BusinessException.Invalid($"Le téléphone a {MaxPhoneLength} caractères au plus.");
            }

            var id = await operation.Connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO customers (name, search_name, phone, created_by)
                VALUES (@name, @searchName, @phone, @EmployeeId)
                RETURNING id
                """,
                new { name, searchName = TextNormalizer.ToSearchKey(name), phone, operation.EmployeeId },
                operation.Transaction);
            await operation.AuditAsync(CustomerCreatedAudit, "customer", id.ToString(), new { nom = name, telephone = phone });
            return new CustomerSummary(id, name, phone);
        });

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
