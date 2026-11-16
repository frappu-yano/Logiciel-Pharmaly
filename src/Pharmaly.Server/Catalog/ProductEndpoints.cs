using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Server.Access;
using Pharmaly.Server.Operations;

namespace Pharmaly.Server.Catalog;

/// <summary>Catalogue : recherche, fiche, création, modification et prix de vente.</summary>
public static partial class ProductEndpoints
{
    public const string CreateOperation = "PRODUIT_CREER";
    public const string UpdateOperation = "PRODUIT_MODIFIER";
    public const string ChangeSalePriceOperation = "PRIX_VENTE_MODIFIER";

    private const int MaxNameLength = 200;
    private const int MaxReferenceNameLength = 100;
    private const int MaxBarcodes = 20;
    private const decimal MaxPrice = 9_999_999_999.99m;

    public static void MapProductEndpoints(this WebApplication app)
    {
        app.MapGet(ApiRoutes.ProductSearch, async (
            string? q, int? limite, bool? inactifs, HttpContext context, ProductQueries queries, CancellationToken cancellationToken) =>
            Results.Ok(await queries.SearchAsync(
                q, limite ?? ProductQueries.DefaultLimit, context.GetIdentity().StoreId, cancellationToken, inactifs ?? false)))
            .RequirePermission(Permissions.ProductsView);

        app.MapGet(ApiRoutes.ProductByBarcode + "/{barcode}", async (
            string barcode, bool? inactifs, HttpContext context, ProductQueries queries, CancellationToken cancellationToken) =>
            await queries.FindByBarcodeAsync(barcode, context.GetIdentity().StoreId, cancellationToken, inactifs ?? false) is { } product
                ? Results.Ok(product)
                : Results.NotFound())
            .RequirePermission(Permissions.ProductsView);

        // Marques et catégories existantes : pour tout employé ayant un droit sur les produits.
        app.MapGet(ApiRoutes.ProductBrands, (
            HttpContext context, NpgsqlDataSource dataSource, ProductQueries queries, CancellationToken cancellationToken) =>
            ListReferencesAsync(context, dataSource, queries, brands: true, cancellationToken)).RequireEmployee();
        app.MapGet(ApiRoutes.ProductCategories, (
            HttpContext context, NpgsqlDataSource dataSource, ProductQueries queries, CancellationToken cancellationToken) =>
            ListReferencesAsync(context, dataSource, queries, brands: false, cancellationToken)).RequireEmployee();

        app.MapGet(ApiRoutes.Products + "/{id:guid}", async (Guid id, ProductQueries queries, CancellationToken cancellationToken) =>
            await queries.ReadDetailAsync(id, cancellationToken) is { } detail
                ? Results.Ok(detail)
                : Results.Json(new ApiError(ApiErrorCodes.NotFound, "Produit introuvable."), statusCode: StatusCodes.Status404NotFound))
            .RequirePermission(Permissions.ProductsView);

        app.MapPost(ApiRoutes.Products, CreateAsync).RequirePermission(Permissions.ProductsCreate);
        app.MapPut(ApiRoutes.Products + "/{id:guid}", UpdateAsync).RequirePermission(Permissions.ProductsEdit);
        app.MapPut(ApiRoutes.Products + "/{id:guid}/" + ApiRoutes.ProductSalePriceSuffix, ChangeSalePriceAsync)
            .RequirePermission(Permissions.SalePriceEdit);
    }

    private static async Task<IResult> ListReferencesAsync(
        HttpContext context, NpgsqlDataSource dataSource, ProductQueries queries, bool brands, CancellationToken cancellationToken) =>
        await PermissionGuard.RefuseWithoutAnyAsync(
            dataSource, context, Permissions.ProductsView, Permissions.ProductsCreate, Permissions.ProductsEdit, Permissions.SalePriceEdit)
        ?? Results.Ok(await queries.ListReferencesAsync(brands, cancellationToken));

    private static Task<IResult> CreateAsync(CreateProductRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, CreateOperation, context.GetIdentity(), request, async operation =>
        {
            var name = CleanName(request.Name);
            ValidatePrice(request.SalePrice);
            var barcodes = CleanBarcodes(request.Barcodes);
            if (barcodes.Count == 0)
            {
                throw BusinessException.Invalid("Au moins un code-barres est obligatoire.");
            }

            var brand = CleanReferenceName(request.Brand);
            var category = CleanReferenceName(request.Category);
            var brandId = await ResolveReferenceAsync(operation, "brands", brand);
            var categoryId = await ResolveReferenceAsync(operation, "categories", category);

            var id = await operation.Connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO products (name, search_name, brand_id, category_id, sale_price, sold_by_unit)
                VALUES (@name, @searchName, @brandId, @categoryId, @SalePrice, @SoldByUnit)
                RETURNING id
                """,
                new { name, searchName = TextNormalizer.ToSearchKey(name), brandId, categoryId, request.SalePrice, request.SoldByUnit },
                operation.Transaction);
            await AddBarcodesAsync(operation, id, barcodes, firstIsPrimary: true);
            await RecordSalePriceAsync(operation, id, oldPrice: null, request.SalePrice);

            await operation.AuditAsync(
                AuditActions.ProductCreated, "product", id.ToString(),
                new { nom = name, codesBarres = barcodes, marque = brand, categorie = category, prixVente = request.SalePrice, venduALUnite = request.SoldByUnit });
            return await ProductQueries.ReadSummaryAsync(operation.Connection, operation.Transaction, id, operation.Identity.StoreId);
        });

    private static Task<IResult> UpdateAsync(Guid id, UpdateProductRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, UpdateOperation, context.GetIdentity(), new { id, request }, async operation =>
        {
            var name = CleanName(request.Name);
            var brand = CleanReferenceName(request.Brand);
            var category = CleanReferenceName(request.Category);
            var toAdd = CleanBarcodes(request.BarcodesToAdd);
            var toRemove = (request.BarcodesToRemove ?? []).Select(barcode => (barcode ?? string.Empty).Trim()).Distinct().ToList();

            var found = (await operation.Connection.QueryAsync<(string Name, string? Brand, string? Category, bool IsActive)>(
                """
                SELECT p.name, b.name, c.name, p.is_active
                FROM products p
                LEFT JOIN brands b ON b.id = p.brand_id
                LEFT JOIN categories c ON c.id = p.category_id
                WHERE p.id = @id
                FOR UPDATE OF p
                """,
                new { id },
                operation.Transaction)).AsList();
            if (found.Count == 0)
            {
                throw BusinessException.NotFound("Produit introuvable.");
            }

            var before = found[0];

            var existing = (await operation.Connection.QueryAsync<string>(
                "SELECT barcode FROM product_barcodes WHERE product_id = @id", new { id }, operation.Transaction)).ToHashSet();
            if (toRemove.Find(barcode => !existing.Contains(barcode)) is { } foreign)
            {
                throw BusinessException.Invalid($"Le code-barres {foreign} n'appartient pas à ce produit.");
            }

            if (toAdd.Find(toRemove.Contains) is { } both)
            {
                throw BusinessException.Invalid($"Le code-barres {both} est à la fois ajouté et retiré.");
            }

            toAdd = toAdd.Where(barcode => !existing.Contains(barcode)).ToList();
            if (toRemove.Count > 0 && existing.Count - toRemove.Count + toAdd.Count == 0)
            {
                throw BusinessException.Invalid("Un produit garde au moins un code-barres.");
            }

            var brandId = await ResolveReferenceAsync(operation, "brands", brand);
            var categoryId = await ResolveReferenceAsync(operation, "categories", category);
            await operation.Connection.ExecuteAsync(
                """
                UPDATE products
                SET name = @name, search_name = @searchName, brand_id = @brandId, category_id = @categoryId,
                    is_active = @IsActive, updated_at = now()
                WHERE id = @id
                """,
                new { id, name, searchName = TextNormalizer.ToSearchKey(name), brandId, categoryId, request.IsActive },
                operation.Transaction);

            if (toRemove.Count > 0)
            {
                await operation.Connection.ExecuteAsync(
                    "DELETE FROM product_barcodes WHERE product_id = @id AND barcode = ANY(@toRemove)",
                    new { id, toRemove = toRemove.ToArray() },
                    operation.Transaction);
            }

            await AddBarcodesAsync(operation, id, toAdd, firstIsPrimary: false);

            // Si le code-barres principal a été retiré, un autre prend sa place.
            await operation.Connection.ExecuteAsync(
                """
                UPDATE product_barcodes SET is_primary = true
                WHERE barcode = (SELECT min(barcode) FROM product_barcodes WHERE product_id = @id)
                  AND NOT EXISTS (SELECT 1 FROM product_barcodes WHERE product_id = @id AND is_primary)
                """,
                new { id },
                operation.Transaction);

            await operation.AuditAsync(
                AuditActions.ProductUpdated, "product", id.ToString(),
                new
                {
                    avant = new { nom = before.Name, marque = before.Brand, categorie = before.Category, actif = before.IsActive },
                    apres = new { nom = name, marque = brand, categorie = category, actif = request.IsActive },
                    codesBarresAjoutes = toAdd,
                    codesBarresRetires = toRemove,
                });
            return await ProductQueries.ReadSummaryAsync(operation.Connection, operation.Transaction, id, operation.Identity.StoreId);
        });

    private static Task<IResult> ChangeSalePriceAsync(Guid id, ChangeSalePriceRequest request, HttpContext context, OperationRunner runner) =>
        runner.RunAsync(request.OperationId, ChangeSalePriceOperation, context.GetIdentity(), new { id, request }, async operation =>
        {
            ValidatePrice(request.NewPrice);
            var oldPrice = await operation.Connection.ExecuteScalarAsync<decimal?>(
                "SELECT sale_price FROM products WHERE id = @id FOR UPDATE", new { id }, operation.Transaction)
                ?? throw BusinessException.NotFound("Produit introuvable.");

            // Un prix identique ne crée ni historique ni audit : rien n'a changé.
            if (oldPrice != request.NewPrice)
            {
                await operation.Connection.ExecuteAsync(
                    "UPDATE products SET sale_price = @NewPrice, updated_at = now() WHERE id = @id",
                    new { id, request.NewPrice },
                    operation.Transaction);
                await RecordSalePriceAsync(operation, id, oldPrice, request.NewPrice);
                await operation.AuditAsync(
                    AuditActions.SalePriceChanged, "product", id.ToString(),
                    new { ancienPrix = oldPrice, nouveauPrix = request.NewPrice });
            }

            return new SalePriceChangeResult(id, oldPrice, request.NewPrice);
        });

    private static Task RecordSalePriceAsync(OperationContext operation, Guid productId, decimal? oldPrice, decimal newPrice) =>
        operation.Connection.ExecuteAsync(
            """
            INSERT INTO product_sale_prices (product_id, old_price, new_price, employee_id, operation_id)
            VALUES (@productId, @oldPrice, @newPrice, @EmployeeId, @OperationId)
            """,
            new { productId, oldPrice, newPrice, operation.EmployeeId, operation.OperationId },
            operation.Transaction);

    private static async Task AddBarcodesAsync(OperationContext operation, Guid productId, List<string> barcodes, bool firstIsPrimary)
    {
        if (barcodes.Count == 0)
        {
            return;
        }

        var taken = (await operation.Connection.QueryAsync<(string Barcode, string ProductName)>(
            """
            SELECT b.barcode, p.name FROM product_barcodes b JOIN products p ON p.id = b.product_id
            WHERE b.barcode = ANY(@barcodes) ORDER BY b.barcode LIMIT 1
            """,
            new { barcodes = barcodes.ToArray() },
            operation.Transaction)).AsList();
        if (taken.Count > 0)
        {
            var used = taken[0];
            throw BusinessException.Conflict($"Le code-barres {used.Barcode} est déjà attribué à « {used.ProductName} ».");
        }

        try
        {
            for (var index = 0; index < barcodes.Count; index++)
            {
                await operation.Connection.ExecuteAsync(
                    "INSERT INTO product_barcodes (barcode, product_id, is_primary) VALUES (@barcode, @productId, @isPrimary)",
                    new { barcode = barcodes[index], productId, isPrimary = firstIsPrimary && index == 0 },
                    operation.Transaction);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Un autre poste vient d'attribuer le même code-barres.
            throw BusinessException.Conflict("Un de ces codes-barres vient d'être attribué à un autre produit.");
        }
    }

    /// <summary>Identifiant de la marque ou de la catégorie portant ce nom, créée si elle n'existe pas.</summary>
    private static async Task<Guid?> ResolveReferenceAsync(OperationContext operation, string table, string? name)
    {
        if (name is null)
        {
            return null;
        }

        return await operation.Connection.ExecuteScalarAsync<Guid?>(
                   $"SELECT id FROM {table} WHERE lower(name) = lower(@name) ORDER BY name LIMIT 1", new { name }, operation.Transaction)
               ?? await operation.Connection.ExecuteScalarAsync<Guid>(
                   $"""
                    INSERT INTO {table} (name) VALUES (@name)
                    ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name
                    RETURNING id
                    """,
                   new { name },
                   operation.Transaction);
    }

    private static string CleanName(string? name)
    {
        var cleaned = (name ?? string.Empty).Trim();
        return cleaned.Length is 0 or > MaxNameLength
            ? throw BusinessException.Invalid($"La désignation est obligatoire ({MaxNameLength} caractères au plus).")
            : cleaned;
    }

    private static string? CleanReferenceName(string? name)
    {
        var cleaned = (name ?? string.Empty).Trim();
        return cleaned.Length switch
        {
            0 => null,
            > MaxReferenceNameLength => throw BusinessException.Invalid(
                $"Un nom de marque ou de catégorie a {MaxReferenceNameLength} caractères au plus."),
            _ => cleaned,
        };
    }

    private static void ValidatePrice(decimal price)
    {
        if (price is < 0 or > MaxPrice || decimal.Round(price, 2) != price)
        {
            throw BusinessException.Invalid("Le prix de vente doit être positif ou nul, avec au plus deux décimales.");
        }
    }

    private static List<string> CleanBarcodes(IReadOnlyList<string>? barcodes)
    {
        var cleaned = (barcodes ?? []).Select(barcode => (barcode ?? string.Empty).Trim()).Distinct().ToList();
        if (cleaned.Count > MaxBarcodes)
        {
            throw BusinessException.Invalid($"Un produit a au plus {MaxBarcodes} codes-barres.");
        }

        if (cleaned.Find(barcode => !BarcodeFormat().IsMatch(barcode)) is { } invalid)
        {
            throw BusinessException.Invalid(
                $"Code-barres invalide : « {invalid} ». Il faut 4 à 64 caractères : chiffres, lettres, point ou tiret.");
        }

        return cleaned;
    }

    // Même règle que la contrainte de la table product_barcodes.
    [GeneratedRegex("^[0-9A-Za-z.-]{4,64}$")]
    private static partial Regex BarcodeFormat();
}
