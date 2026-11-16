using CommunityToolkit.Mvvm.ComponentModel;
using Pharmaly.Contracts;
using Pharmaly.Poste.Services.Server;

namespace Pharmaly.Poste.Modules.SupplierNotes;

/// <summary>
/// Création rapide d'un produit inconnu scanné pendant la saisie d'un bon : le strict
/// nécessaire pour continuer la saisie. La fiche complète se règle dans le module Produits.
/// </summary>
public sealed partial class QuickProductViewModel(ISupplierNotesClient notes, ServerCaller call, string barcode) : ObservableObject
{
    private CreateProductRequest? lastRequest;

    /// <summary>Code-barres scanné : il devient le code-barres du produit.</summary>
    public string Barcode { get; } = barcode;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string salePriceText = string.Empty;

    [ObservableProperty]
    private string brand = string.Empty;

    [ObservableProperty]
    private string category = string.Empty;

    [ObservableProperty]
    private bool soldByUnit = true;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>Crée le produit. Renvoie le produit prêt pour la saisie, ou <c>null</c> si rien n'a été créé.</summary>
    public async Task<NoteProductInfo?> CreateAsync()
    {
        if (IsBusy)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "La désignation est obligatoire.";
            return null;
        }

        if (NoteInput.ReadPrice(SalePriceText, out var salePrice) is { } invalid)
        {
            ErrorMessage = invalid;
            return null;
        }

        // Même demande relancée après une panne : même identifiant, le produit n'est créé qu'une fois.
        var operationId = lastRequest is { } sent
            && sent.Name == Name.Trim() && sent.SalePrice == salePrice && sent.Brand == Clean(Brand)
            && sent.Category == Clean(Category) && sent.SoldByUnit == SoldByUnit
                ? sent.OperationId
                : Guid.NewGuid();
        var request = new CreateProductRequest(operationId, Name.Trim(), [Barcode], Clean(Brand), Clean(Category), salePrice, SoldByUnit);
        lastRequest = request;

        IsBusy = true;
        ErrorMessage = string.Empty;
        ProductSummary? created = null;
        var result = await call(async () => created = await notes.CreateQuickProductAsync(request, CancellationToken.None));
        IsBusy = false;

        if (!result.Succeeded)
        {
            ErrorMessage = result.Refusal?.Message
                ?? $"{ModuleViewModel.ServerUnreachableMessage} Le produit n'est peut-être pas créé : appuyez sur Entrée pour réessayer.";
            return null;
        }

        // Un produit qui vient d'être créé n'a jamais été acheté : pas de dernier prix d'achat.
        return new NoteProductInfo(
            created!.Id, created.Code, created.Name, created.Barcode ?? Barcode, created.SalePrice, SoldByUnit, true, null, null, null);
    }

    private static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
