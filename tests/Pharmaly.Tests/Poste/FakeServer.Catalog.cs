using System.Text.Json;
using Pharmaly.Contracts;
using Pharmaly.Domain;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Tests.Poste;

/// <summary>Catalogue : mêmes refus, mêmes messages et même idempotence que le vrai serveur.</summary>
internal sealed partial class FakeServer : ICatalogClient
{
    public const decimal CremeStock = 12;
    public const decimal SerumStock = 3;

    public static readonly ProductSummary Creme =
        new(Guid.NewGuid(), 100001, "Crème hydratante 50 ml", 1850.10m, "2000000000015", CremeStock);

    public static readonly ProductSummary Serum =
        new(Guid.NewGuid(), 100002, "Sérum nuit 30 ml", 4200.20m, "2000000000022", SerumStock);

    private sealed class FakeProduct
    {
        public required Guid Id { get; init; }
        public required long Code { get; init; }
        public required string Name { get; set; }
        public string? Brand { get; set; }
        public string? Category { get; set; }
        public required decimal SalePrice { get; set; }
        public bool SoldByUnit { get; init; } = true;
        public bool IsActive { get; set; } = true;
        public List<string> Barcodes { get; } = [];
        public List<SalePriceHistoryLine> Prices { get; } = [];
        public List<StockMovementLine> Movements { get; } = [];
        public Dictionary<Guid, decimal> Stock { get; } = [];
    }

    private readonly List<FakeProduct> products = [];
    private readonly Dictionary<Guid, (string Request, object Result)> operations = [];
    private long nextMovementId = 1;

    /// <summary>Demandes d'écriture envoyées par le poste, dans l'ordre : perdues par le réseau, refusées et rejouées comprises.</summary>
    public List<CreateProductRequest> ProductCreations { get; } = [];

    public List<(Guid ProductId, UpdateProductRequest Request)> ProductUpdates { get; } = [];

    public List<(Guid ProductId, ChangeSalePriceRequest Request)> PriceChanges { get; } = [];

    /// <summary>
    /// Simule une coupure du réseau juste après que le serveur a enregistré la prochaine
    /// écriture : le poste ne reçoit pas la réponse et ne sait pas si elle a été prise en compte.
    /// </summary>
    public bool LoseNextResponse { get; set; }

    /// <summary>Quand il est renseigné, chaque écriture attend qu'il soit complété (serveur lent).</summary>
    public TaskCompletionSource? HoldWrites { get; set; }

    /// <summary>
    /// Stock vendable du site du poste (emplacement principal), lu et écrit par les ventes.
    /// C'est le même stock que celui des fiches produits : une seule vérité, comme sur le serveur.
    /// </summary>
    public MainStock StockLevels => new(this);

    public sealed class MainStock(FakeServer server)
    {
        public decimal this[Guid productId]
        {
            get => server.Find(productId).Stock.GetValueOrDefault(MainLocation.Id);
            set => server.Find(productId).Stock[MainLocation.Id] = value;
        }

        public decimal GetValueOrDefault(Guid productId) =>
            server.products.FirstOrDefault(product => product.Id == productId)?.Stock.GetValueOrDefault(MainLocation.Id) ?? 0;
    }

    /// <summary>Fixe le stock vendable de départ de la crème et du sérum (les tests de vente partent de 10).</summary>
    public FakeServer WithStock(decimal creme, decimal serum)
    {
        StockLevels[Creme.Id] = creme;
        StockLevels[Serum.Id] = serum;
        return this;
    }

    /// <summary>Simule un changement de prix de vente fait depuis un autre poste.</summary>
    public void ChangeSalePriceElsewhere(Guid productId, decimal newPrice) => Find(productId).SalePrice = newPrice;

    /// <summary>Simule un employé sans le droit de consulter les produits.</summary>
    public bool DenyProducts
    {
        get => !EmployeePermissions.Contains(Permissions.ProductsView);
        set => EmployeePermissions = value
            ? EmployeePermissions.Where(code => code != Permissions.ProductsView).ToList()
            : EmployeePermissions.Append(Permissions.ProductsView).Distinct().ToList();
    }

    public FakeServer() => SeedCatalog();

    private void SeedCatalog()
    {
        foreach (var (summary, brand, category) in new[] { (Creme, "Dermalia", "Soin visage"), (Serum, "Botaniq", "Soin visage") })
        {
            var product = new FakeProduct
            {
                Id = summary.Id, Code = summary.Code, Name = summary.Name, SalePrice = summary.SalePrice, Brand = brand, Category = category,
            };
            product.Barcodes.Add(summary.Barcode!);
            product.Prices.Add(new SalePriceHistoryLine(DateTimeOffset.Now.AddDays(-30), null, summary.SalePrice, KarimName));
            product.Stock[MainLocation.Id] = summary.SellableStock;
            product.Movements.Add(new StockMovementLine(
                nextMovementId++, DateTimeOffset.Now.AddDays(-2), MainLocation.Id, MainLocation.Name, StockMovementTypes.NoteEntry,
                summary.SellableStock, summary.SellableStock, KarimName, "BON", "B-1", null));
            products.Add(product);
        }
    }

    /// <summary>Ajoute un produit au catalogue du faux serveur (par exemple un produit vendu au poids).</summary>
    public ProductSummary AddProduct(string name, string barcode, decimal price, bool soldByUnit, decimal mainStock = 0)
    {
        var product = new FakeProduct
        {
            Id = Guid.NewGuid(), Code = 100001 + products.Count, Name = name, SalePrice = price, SoldByUnit = soldByUnit,
        };
        product.Barcodes.Add(barcode);
        product.Prices.Add(new SalePriceHistoryLine(DateTimeOffset.Now, null, price, EmployeeName));
        product.Stock[MainLocation.Id] = mainStock;
        products.Add(product);
        return Summarize(product);
    }

    /// <summary>Stock d'un produit à un emplacement, tel que le faux serveur le tient.</summary>
    public decimal StockOf(Guid productId, Guid locationId) => Find(productId).Stock.GetValueOrDefault(locationId);

    private ProductSummary Summarize(FakeProduct product) => new(
        product.Id, product.Code, product.Name, product.SalePrice, product.Barcodes.FirstOrDefault(),
        product.Stock.GetValueOrDefault(MainLocation.Id));

    private FakeProduct Find(Guid productId) =>
        products.FirstOrDefault(product => product.Id == productId)
        ?? throw new ServerRefusedException(ApiErrorCodes.NotFound, "Produit introuvable.");

    /// <summary>
    /// Idempotence du serveur : le même identifiant avec la même demande renvoie le résultat
    /// déjà enregistré sans rien refaire ; avec une autre demande, il est refusé. Une demande
    /// refusée n'enregistre rien.
    /// </summary>
    private async Task<TResult> RunOperationAsync<TResult>(Guid operationId, object requestWithoutId, Func<TResult> action)
        where TResult : class
    {
        if (HoldWrites is { } hold)
        {
            await hold.Task;
        }

        EnsureUp();
        if (operationId == Guid.Empty)
        {
            throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Identifiant d'opération manquant.");
        }

        var request = $"{requestWithoutId.GetType().Name}\n{JsonSerializer.Serialize(requestWithoutId, requestWithoutId.GetType())}";
        TResult result;
        if (operations.TryGetValue(operationId, out var known))
        {
            if (known.Request != request)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.OperationMismatch,
                    "Cet identifiant d'opération a déjà servi pour une autre demande. Rien n'a été enregistré.");
            }

            result = (TResult)known.Result;
        }
        else
        {
            result = action();
            operations[operationId] = (request, result);
        }

        if (LoseNextResponse)
        {
            LoseNextResponse = false;
            throw new HttpRequestException("réponse perdue");
        }

        return result;
    }

    public Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(string term, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        var digits = term.Length >= 4 && term.All(char.IsAsciiDigit);
        return Task.FromResult<IReadOnlyList<ProductSummary>>(products
            .Where(product => product.IsActive)
            .Where(product => digits
                ? product.Code.ToString() == term || product.Barcodes.Any(barcode => barcode.StartsWith(term, StringComparison.Ordinal))
                : product.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(Summarize)
            .ToList());
    }

    /// <summary>Quand il est défini, chaque lecture de code-barres attend qu'il soit terminé (serveur lent).</summary>
    public TaskCompletionSource? BarcodeGate { get; set; }

    public async Task<ProductSummary?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        if (BarcodeGate is { } gate)
        {
            await gate.Task;
        }

        EnsurePermission(Permissions.ProductsView);
        var found = products.FirstOrDefault(product => product.IsActive && product.Barcodes.Contains(barcode));
        return found is null ? null : Summarize(found);
    }

    public Task<ProductDetail> GetProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        EnsurePermission(Permissions.ProductsView);
        var product = Find(productId);
        return Task.FromResult(new ProductDetail(
            product.Id, product.Code, product.Name, product.Brand, product.Category, product.SalePrice, product.SoldByUnit,
            product.IsActive, product.Barcodes.ToList(),
            Locations.Select(location => new ProductStockLine(
                location.Id, location.Code, location.Name, location.Kind, location.StoreId, location.StoreName,
                product.Stock.GetValueOrDefault(location.Id))).ToList(),
            product.Movements.OrderByDescending(movement => movement.Id).ToList(),
            product.Prices.AsEnumerable().Reverse().ToList()));
    }

    public Task<ProductSummary> CreateProductAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        ProductCreations.Add(request);
        EnsurePermission(Permissions.ProductsCreate);
        return RunOperationAsync(request.OperationId, request with { OperationId = Guid.Empty }, () =>
        {
            if (request.Name.Length == 0)
            {
                throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "La désignation est obligatoire (200 caractères au plus).");
            }

            if (request.Barcodes.Count == 0)
            {
                throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Au moins un code-barres est obligatoire.");
            }

            EnsureBarcodesFree(request.Barcodes);
            var product = new FakeProduct
            {
                Id = Guid.NewGuid(), Code = 100001 + products.Count, Name = request.Name, Brand = request.Brand,
                Category = request.Category, SalePrice = request.SalePrice, SoldByUnit = request.SoldByUnit,
            };
            product.Barcodes.AddRange(request.Barcodes);
            product.Prices.Add(new SalePriceHistoryLine(DateTimeOffset.Now, null, request.SalePrice, EmployeeName));
            products.Add(product);
            return Summarize(product);
        });
    }

    public Task<ProductSummary> UpdateProductAsync(Guid productId, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        ProductUpdates.Add((productId, request));
        EnsurePermission(Permissions.ProductsEdit);
        return RunOperationAsync(request.OperationId, new { productId, request = request with { OperationId = Guid.Empty } }, () =>
        {
            var product = Find(productId);
            var toAdd = (request.BarcodesToAdd ?? []).Where(barcode => !product.Barcodes.Contains(barcode)).ToList();
            var toRemove = request.BarcodesToRemove ?? [];
            if (toRemove.Count > 0 && product.Barcodes.Count - toRemove.Count + toAdd.Count == 0)
            {
                throw new ServerRefusedException(ApiErrorCodes.InvalidRequest, "Un produit garde au moins un code-barres.");
            }

            EnsureBarcodesFree(toAdd);
            product.Name = request.Name;
            product.Brand = request.Brand;
            product.Category = request.Category;
            product.IsActive = request.IsActive;
            product.Barcodes.RemoveAll(toRemove.Contains);
            product.Barcodes.AddRange(toAdd);
            return Summarize(product);
        });
    }

    public Task<SalePriceChangeResult> ChangeSalePriceAsync(
        Guid productId, ChangeSalePriceRequest request, CancellationToken cancellationToken)
    {
        PriceChanges.Add((productId, request));
        EnsurePermission(Permissions.SalePriceEdit);
        return RunOperationAsync(request.OperationId, new { productId, request.NewPrice }, () =>
        {
            var product = Find(productId);
            var old = product.SalePrice;
            product.SalePrice = request.NewPrice;
            product.Prices.Add(new SalePriceHistoryLine(DateTimeOffset.Now, old, request.NewPrice, EmployeeName));
            return new SalePriceChangeResult(productId, old, request.NewPrice);
        });
    }

    private void EnsureBarcodesFree(IEnumerable<string> barcodes)
    {
        foreach (var barcode in barcodes.Order())
        {
            if (products.FirstOrDefault(product => product.Barcodes.Contains(barcode)) is { } owner)
            {
                throw new ServerRefusedException(
                    ApiErrorCodes.Conflict, $"Le code-barres {barcode} est déjà attribué à « {owner.Name} ».");
            }
        }
    }
}
