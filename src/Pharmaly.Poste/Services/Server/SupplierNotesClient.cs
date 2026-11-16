using System.Globalization;
using System.Text;
using Pharmaly.Contracts;

namespace Pharmaly.Poste.Services.Server;

/// <summary>Filtres de la liste des bons ; <c>null</c> = pas de filtre.</summary>
public sealed record NoteListFilter(string? Status, Guid? SupplierId, DateOnly? From, DateOnly? To, int Page);

/// <summary>
/// Module « bons fournisseurs » : fournisseurs, bons, verrou de saisie, lignes, et ce dont
/// la saisie a besoin autour (emplacements, création rapide d'un produit, stock d'un produit).
/// </summary>
public interface ISupplierNotesClient
{
    Task<IReadOnlyList<SupplierSummary>> ListSuppliersAsync(string? term, CancellationToken cancellationToken);

    Task<SupplierSummary> CreateSupplierAsync(CreateSupplierRequest request, CancellationToken cancellationToken);

    /// <summary>Emplacements de stock où un bon peut faire entrer la marchandise.</summary>
    Task<IReadOnlyList<StockLocationSummary>> ListNoteLocationsAsync(CancellationToken cancellationToken);

    Task<SupplierNotePage> ListNotesAsync(NoteListFilter filter, CancellationToken cancellationToken);

    /// <summary>Le bon créé est aussitôt réservé à l'employé.</summary>
    Task<SupplierNoteDetail> CreateNoteAsync(CreateSupplierNoteRequest request, CancellationToken cancellationToken);

    /// <summary>Lecture seule : ne prend pas le verrou.</summary>
    Task<SupplierNoteDetail> ReadNoteAsync(Guid noteId, CancellationToken cancellationToken);

    /// <summary>Prend ou reprend le verrou ; refus <c>BON_VERROUILLE</c> si un autre employé tient le bon.</summary>
    Task<SupplierNoteDetail> AcquireNoteLockAsync(Guid noteId, CancellationToken cancellationToken);

    /// <summary>Battement : prolonge le bail du verrou.</summary>
    Task RenewNoteLockAsync(Guid noteId, CancellationToken cancellationToken);

    Task ReleaseNoteLockAsync(Guid noteId, CancellationToken cancellationToken);

    /// <summary>Produit scanné, avec son dernier prix d'achat ; refus <c>PRODUIT_INCONNU</c> si le code n'existe pas.</summary>
    Task<NoteProductInfo> FindNoteProductByBarcodeAsync(string barcode, CancellationToken cancellationToken);

    /// <summary>Même réponse, pour un produit choisi par son nom.</summary>
    Task<NoteProductInfo> ReadNoteProductAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Stock du produit, emplacement par emplacement (droit de consulter les produits).</summary>
    Task<IReadOnlyList<ProductStockLine>> ReadNoteProductStockAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Création rapide d'un produit inconnu pendant la saisie (droit de créer un produit).</summary>
    Task<ProductSummary> CreateQuickProductAsync(CreateProductRequest request, CancellationToken cancellationToken);

    Task<SupplierNoteLineResult> AddNoteLineAsync(Guid noteId, AddSupplierNoteLineRequest request, CancellationToken cancellationToken);

    Task<SupplierNoteLineResult> UpdateNoteLineAsync(
        Guid noteId, Guid lineId, UpdateSupplierNoteLineRequest request, CancellationToken cancellationToken);

    Task<SupplierNoteLineResult> CancelNoteLineAsync(
        Guid noteId, Guid lineId, CancelSupplierNoteLineRequest request, CancellationToken cancellationToken);

    Task<SupplierNoteSummary> ValidateNoteAsync(Guid noteId, ValidateSupplierNoteRequest request, CancellationToken cancellationToken);

    Task<SupplierNoteSummary> CancelNoteAsync(Guid noteId, CancelSupplierNoteRequest request, CancellationToken cancellationToken);
}

public sealed class SupplierNotesClient(IServerTransport transport) : ISupplierNotesClient
{
    private const string DateFormat = "yyyy-MM-dd";

    public async Task<IReadOnlyList<SupplierSummary>> ListSuppliersAsync(string? term, CancellationToken cancellationToken)
    {
        var route = string.IsNullOrWhiteSpace(term)
            ? SupplierNoteRoutes.Suppliers
            : $"{SupplierNoteRoutes.Suppliers}?q={Uri.EscapeDataString(term.Trim())}";
        return await transport.SendAsync<List<SupplierSummary>>(HttpMethod.Get, route, null, cancellationToken) ?? [];
    }

    public async Task<SupplierSummary> CreateSupplierAsync(CreateSupplierRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierSummary>(HttpMethod.Post, SupplierNoteRoutes.Suppliers, request, cancellationToken))!;

    public async Task<IReadOnlyList<StockLocationSummary>> ListNoteLocationsAsync(CancellationToken cancellationToken) =>
        await transport.SendAsync<List<StockLocationSummary>>(HttpMethod.Get, ApiRoutes.StockLocations, null, cancellationToken) ?? [];

    public async Task<SupplierNotePage> ListNotesAsync(NoteListFilter filter, CancellationToken cancellationToken)
    {
        var route = new StringBuilder(SupplierNoteRoutes.Notes)
            .Append('?').Append(SupplierNoteRoutes.PageParameter).Append('=').Append(Math.Max(1, filter.Page));
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                route.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        Add(SupplierNoteRoutes.StatusParameter, filter.Status);
        Add(SupplierNoteRoutes.SupplierParameter, filter.SupplierId?.ToString());
        Add(SupplierNoteRoutes.FromParameter, filter.From?.ToString(DateFormat, CultureInfo.InvariantCulture));
        Add(SupplierNoteRoutes.ToParameter, filter.To?.ToString(DateFormat, CultureInfo.InvariantCulture));
        return (await transport.SendAsync<SupplierNotePage>(HttpMethod.Get, route.ToString(), null, cancellationToken))!;
    }

    public async Task<SupplierNoteDetail> CreateNoteAsync(CreateSupplierNoteRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteDetail>(HttpMethod.Post, SupplierNoteRoutes.Notes, request, cancellationToken))!;

    public async Task<SupplierNoteDetail> ReadNoteAsync(Guid noteId, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteDetail>(HttpMethod.Get, SupplierNoteRoutes.Note(noteId), null, cancellationToken))!;

    public async Task<SupplierNoteDetail> AcquireNoteLockAsync(Guid noteId, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteDetail>(HttpMethod.Post, SupplierNoteRoutes.Lock(noteId), null, cancellationToken))!;

    public Task RenewNoteLockAsync(Guid noteId, CancellationToken cancellationToken) =>
        transport.SendAsync(HttpMethod.Put, SupplierNoteRoutes.Lock(noteId), null, cancellationToken);

    public Task ReleaseNoteLockAsync(Guid noteId, CancellationToken cancellationToken) =>
        transport.SendAsync(HttpMethod.Delete, SupplierNoteRoutes.Lock(noteId), null, cancellationToken);

    public async Task<NoteProductInfo> FindNoteProductByBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
        (await transport.SendAsync<NoteProductInfo>(
            HttpMethod.Get, SupplierNoteRoutes.ProductInfoByBarcode(barcode), null, cancellationToken))!;

    public async Task<NoteProductInfo> ReadNoteProductAsync(Guid productId, CancellationToken cancellationToken) =>
        (await transport.SendAsync<NoteProductInfo>(HttpMethod.Get, SupplierNoteRoutes.ProductInfo(productId), null, cancellationToken))!;

    public async Task<IReadOnlyList<ProductStockLine>> ReadNoteProductStockAsync(Guid productId, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ProductDetail>(HttpMethod.Get, ApiRoutes.Product(productId), null, cancellationToken))?.Stock ?? [];

    public async Task<ProductSummary> CreateQuickProductAsync(CreateProductRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<ProductSummary>(HttpMethod.Post, ApiRoutes.Products, request, cancellationToken))!;

    public async Task<SupplierNoteLineResult> AddNoteLineAsync(
        Guid noteId, AddSupplierNoteLineRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteLineResult>(HttpMethod.Post, SupplierNoteRoutes.Lines(noteId), request, cancellationToken))!;

    public async Task<SupplierNoteLineResult> UpdateNoteLineAsync(
        Guid noteId, Guid lineId, UpdateSupplierNoteLineRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteLineResult>(
            HttpMethod.Put, SupplierNoteRoutes.Line(noteId, lineId), request, cancellationToken))!;

    public async Task<SupplierNoteLineResult> CancelNoteLineAsync(
        Guid noteId, Guid lineId, CancelSupplierNoteLineRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteLineResult>(
            HttpMethod.Post, SupplierNoteRoutes.LineCancellation(noteId, lineId), request, cancellationToken))!;

    public async Task<SupplierNoteSummary> ValidateNoteAsync(
        Guid noteId, ValidateSupplierNoteRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteSummary>(HttpMethod.Post, SupplierNoteRoutes.Validation(noteId), request, cancellationToken))!;

    public async Task<SupplierNoteSummary> CancelNoteAsync(
        Guid noteId, CancelSupplierNoteRequest request, CancellationToken cancellationToken) =>
        (await transport.SendAsync<SupplierNoteSummary>(HttpMethod.Post, SupplierNoteRoutes.Cancellation(noteId), request, cancellationToken))!;
}
